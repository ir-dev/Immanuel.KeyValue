using System.Collections.Concurrent;
using Ark.Rapid;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Immanuel.KeyValue.Core;

/// <summary>
/// Hands out one <see cref="SqliteManager"/> (the Ark.Rapid.Database SQLite provider) per app
/// key, creating and migrating that app key's database file the first time it is asked for.
/// Managers are cached, so the schema work happens once per app key per process.
///
/// An app key's file lives either directly in the data directory - anonymous keys, and everything
/// migrated from v1 - or in the folder belonging to the account that owns it:
/// <code>
/// App_Data/3cg7aby9.db                        anonymous
/// App_Data/raj_at_immanuel.co/pk4m2xn8.db     owned by raj@immanuel.co
/// </code>
/// App keys stay globally unique either way, so callers never have to say which folder they mean;
/// the factory resolves it once and caches the answer.
/// </summary>
public sealed class SqliteStoreFactory
{
    private readonly KeyValueOptions _options;
    private readonly ILogger<SqliteStoreFactory> _logger;

    // Lazy<Task<T>> rather than a plain Task<T>: ConcurrentDictionary may run its value factory
    // more than once under contention, and provisioning twice would be wasted work. Lazy with the
    // default thread-safety mode guarantees exactly one provisioning run per app key.
    private readonly ConcurrentDictionary<string, Lazy<Task<SqliteManager>>> _stores = new();

    // app key -> the directory its file is in. Only ever holds directories we have seen a file in,
    // so a hit still gets a File.Exists check before it is trusted.
    private readonly ConcurrentDictionary<string, string> _locations = new(StringComparer.Ordinal);

    private readonly Lazy<Task<SqliteManager>> _catalog;
    private readonly Lazy<Task<SqliteManager>> _users;

    public string DataDirectory { get; }

    public SqliteStoreFactory(
        IOptions<KeyValueOptions> options,
        string contentRootPath,
        ILogger<SqliteStoreFactory> logger)
    {
        _options = options.Value;
        _logger = logger;

        DataDirectory = Path.IsPathRooted(_options.DataDirectory)
            ? _options.DataDirectory
            : Path.Combine(contentRootPath, _options.DataDirectory);

        Directory.CreateDirectory(DataDirectory);
        _catalog = new Lazy<Task<SqliteManager>>(ProvisionCatalogAsync);
        _users = new Lazy<Task<SqliteManager>>(ProvisionUsersAsync);

        _logger.LogInformation("Key-value data directory: {DataDirectory}", DataDirectory);
    }

    /// <summary>
    /// Full path of the database backing <paramref name="appKey"/> inside
    /// <paramref name="userFolder"/>, or directly in the data directory when that is null.
    /// </summary>
    public string PathFor(string appKey, string? userFolder = null)
    {
        if (!AppKey.IsValid(appKey))
        {
            throw new ArgumentException($"'{appKey}' is not a valid app key.", nameof(appKey));
        }

        if (userFolder is null) return Path.Combine(DataDirectory, $"{appKey}.db");

        if (!UserFolder.IsValidFolderName(userFolder))
        {
            throw new ArgumentException($"'{userFolder}' is not a valid user folder.", nameof(userFolder));
        }

        return Path.Combine(DataDirectory, userFolder, $"{appKey}.db");
    }

    /// <summary>True when this app key already has a database on disk, wherever it lives.</summary>
    public bool Exists(string appKey) => LocateDirectory(appKey) is not null;

    /// <summary>
    /// The directory holding this app key's file, or null when no such file exists. The data
    /// directory is checked first, then each user folder; the answer is cached, so the scan only
    /// happens once per app key per process.
    /// </summary>
    public string? LocateDirectory(string appKey)
    {
        if (!AppKey.IsValid(appKey)) return null;

        var fileName = $"{appKey}.db";

        if (_locations.TryGetValue(appKey, out var cached))
        {
            if (File.Exists(Path.Combine(cached, fileName))) return cached;
            _locations.TryRemove(appKey, out _);
        }

        if (File.Exists(Path.Combine(DataDirectory, fileName))) return Remember(appKey, DataDirectory);

        foreach (var folder in EnumerateUserFolders())
        {
            if (File.Exists(Path.Combine(folder, fileName))) return Remember(appKey, folder);
        }

        return null;
    }

    /// <summary>The account that owns an app key, from where its file sits, or null when the key
    /// is anonymous or unknown. Read off the file system so it costs no query.</summary>
    public string? OwnerOf(string appKey)
    {
        var directory = LocateDirectory(appKey);
        if (directory is null || PathsEqual(directory, DataDirectory)) return null;

        return UserFolder.ToEmail(Path.GetFileName(directory));
    }

    /// <summary>
    /// Opens the database for one app key, creating it in the data directory if it does not
    /// exist yet. Use <see cref="CreateAsync"/> when the new file belongs to an account.
    /// </summary>
    public Task<SqliteManager> OpenAsync(string appKey) => OpenAtAsync(appKey, LocateDirectory(appKey));

    /// <summary>Creates and opens the database for a newly issued app key, inside
    /// <paramref name="userFolder"/> when the key belongs to an account.</summary>
    public Task<SqliteManager> CreateAsync(string appKey, string? userFolder)
    {
        var directory = userFolder is null
            ? DataDirectory
            : Path.Combine(DataDirectory, userFolder);

        if (userFolder is not null)
        {
            if (!UserFolder.IsValidFolderName(userFolder))
            {
                throw new ArgumentException($"'{userFolder}' is not a valid user folder.", nameof(userFolder));
            }

            Directory.CreateDirectory(directory);
        }

        return OpenAtAsync(appKey, directory);
    }

    /// <summary>Opens the shared catalog database that tracks every issued app key.</summary>
    public Task<SqliteManager> OpenCatalogAsync() => _catalog.Value;

    /// <summary>Opens the accounts database holding users, one-time passwords and sessions.</summary>
    public Task<SqliteManager> OpenUsersAsync() => _users.Value;

    /// <summary>Every app key that has a database anywhere under the data directory.</summary>
    public IEnumerable<string> EnumerateAppKeys()
    {
        foreach (var key in AppKeysIn(DataDirectory)) yield return key;

        foreach (var folder in EnumerateUserFolders())
        {
            foreach (var key in AppKeysIn(folder)) yield return key;
        }
    }

    /// <summary>Every app key stored in one account's folder.</summary>
    public IEnumerable<string> EnumerateAppKeys(string userFolder) =>
        UserFolder.IsValidFolderName(userFolder)
            ? AppKeysIn(Path.Combine(DataDirectory, userFolder))
            : [];

    /// <summary>
    /// Deletes an app key's database outright. Returns false when there was nothing to delete.
    /// The connection pool is cleared first, because a pooled handle would otherwise keep the
    /// file locked on Windows and leave the -wal/-shm files behind everywhere.
    /// </summary>
    public bool Delete(string appKey)
    {
        var directory = LocateDirectory(appKey);
        if (directory is null) return false;

        _stores.TryRemove(appKey, out _);
        _locations.TryRemove(appKey, out _);
        SqliteConnection.ClearAllPools();

        var path = Path.Combine(directory, $"{appKey}.db");

        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not delete {File} for app key {AppKey}", file, appKey);
            }
        }

        return !File.Exists(path);
    }

    private IEnumerable<string> EnumerateUserFolders() =>
        Directory.EnumerateDirectories(DataDirectory)
            .Where(path => UserFolder.IsValidFolderName(Path.GetFileName(path)));

    private static IEnumerable<string> AppKeysIn(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.db")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(AppKey.IsValid)
                .Select(name => name!)
            : [];

    private string Remember(string appKey, string directory)
    {
        _locations[appKey] = directory;
        return directory;
    }

    private Task<SqliteManager> OpenAtAsync(string appKey, string? directory)
    {
        var target = directory ?? DataDirectory;

        var entry = _stores.GetOrAdd(
            appKey,
            key => new Lazy<Task<SqliteManager>>(() => ProvisionAppKeyAsync(key, target)));

        return AwaitAndForgetOnFailure(appKey, entry);
    }

    private async Task<SqliteManager> AwaitAndForgetOnFailure(string appKey, Lazy<Task<SqliteManager>> entry)
    {
        try
        {
            return await entry.Value;
        }
        catch
        {
            // Don't let one failed provisioning attempt poison the cache forever.
            _stores.TryRemove(new KeyValuePair<string, Lazy<Task<SqliteManager>>>(appKey, entry));
            throw;
        }
    }

    private async Task<SqliteManager> ProvisionAppKeyAsync(string appKey, string directory)
    {
        if (!AppKey.IsValid(appKey))
        {
            throw new ArgumentException($"'{appKey}' is not a valid app key.", nameof(appKey));
        }

        var db = new SqliteManager(BuildConnectionString(Path.Combine(directory, $"{appKey}.db")));

        await EnableWalAsync(db);
        await db.CreateTableAsync(Schema.CreateKeyValTable);
        await db.ExecuteAsync(Schema.CreateKeyValIndex);

        Remember(appKey, directory);

        _logger.LogDebug("Provisioned database for app key {AppKey} in {Directory}", appKey, directory);
        return db;
    }

    private async Task<SqliteManager> ProvisionCatalogAsync()
    {
        var db = new SqliteManager(BuildConnectionString(Path.Combine(DataDirectory, Schema.CatalogFileName)));

        await EnableWalAsync(db);
        await db.CreateTableAsync(Schema.CreateAppKeyTable);

        // Catalogs written by a build that predates account ownership are missing the column.
        if (!await HasColumnAsync(db, "AppKey", "OwnerEmail"))
        {
            await db.ExecuteAsync(Schema.AddAppKeyOwnerColumn);
            _logger.LogInformation("Added OwnerEmail to the existing app key catalog.");
        }

        await db.ExecuteAsync(Schema.CreateAppKeyOwnerIndex);

        return db;
    }

    private async Task<SqliteManager> ProvisionUsersAsync()
    {
        var db = new SqliteManager(BuildConnectionString(Path.Combine(DataDirectory, Schema.UsersFileName)));

        await EnableWalAsync(db);
        await db.CreateTableAsync(Schema.CreateUserTable);
        await db.CreateTableAsync(Schema.CreateOtpTable);
        await db.CreateTableAsync(Schema.CreateSessionTable);
        await db.ExecuteAsync(Schema.CreateUserHeaderIndex);
        await db.ExecuteAsync(Schema.CreateSessionEmailIndex);

        return db;
    }

    /// <summary>PRAGMA table_info is SQLite's stand-in for "does this column exist" - there is no
    /// ADD COLUMN IF NOT EXISTS to lean on.</summary>
    private static async Task<bool> HasColumnAsync(SqliteManager db, string table, string column) =>
        await db.ExecuteCountAsync(
            $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';") > 0;

    /// <summary>
    /// Write-ahead logging lets readers keep working while a write is in flight, which is what
    /// makes a file-per-tenant design hold up under concurrent traffic. The setting is persisted
    /// inside the database file, so applying it once at provisioning time is enough.
    /// </summary>
    private static Task EnableWalAsync(SqliteManager db) => db.ExecuteAsync("PRAGMA journal_mode=WAL;");

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.Ordinal);

    private static string BuildConnectionString(string path) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            // Wait rather than fail instantly when another writer holds the file lock.
            DefaultTimeout = 30,
        }.ToString();
}
