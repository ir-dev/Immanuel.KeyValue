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
/// </summary>
public sealed class SqliteStoreFactory
{
    private readonly KeyValueOptions _options;
    private readonly ILogger<SqliteStoreFactory> _logger;

    // Lazy<Task<T>> rather than a plain Task<T>: ConcurrentDictionary may run its value factory
    // more than once under contention, and provisioning twice would be wasted work. Lazy with the
    // default thread-safety mode guarantees exactly one provisioning run per app key.
    private readonly ConcurrentDictionary<string, Lazy<Task<SqliteManager>>> _stores = new();
    private readonly Lazy<Task<SqliteManager>> _catalog;

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

        _logger.LogInformation("Key-value data directory: {DataDirectory}", DataDirectory);
    }

    /// <summary>Full path of the database backing <paramref name="appKey"/>.</summary>
    public string PathFor(string appKey)
    {
        if (!AppKey.IsValid(appKey))
        {
            throw new ArgumentException($"'{appKey}' is not a valid app key.", nameof(appKey));
        }

        return Path.Combine(DataDirectory, $"{appKey}.db");
    }

    /// <summary>True when this app key already has a database on disk.</summary>
    public bool Exists(string appKey) => AppKey.IsValid(appKey) && File.Exists(PathFor(appKey));

    /// <summary>Opens (creating on first use) the database for one app key.</summary>
    public Task<SqliteManager> OpenAsync(string appKey)
    {
        var entry = _stores.GetOrAdd(
            appKey,
            key => new Lazy<Task<SqliteManager>>(() => ProvisionAppKeyAsync(key)));

        return AwaitAndForgetOnFailure(appKey, entry);
    }

    /// <summary>Opens the shared catalog database that tracks every issued app key.</summary>
    public Task<SqliteManager> OpenCatalogAsync() => _catalog.Value;

    /// <summary>Every app key that has a database in the data directory.</summary>
    public IEnumerable<string> EnumerateAppKeys() =>
        Directory.EnumerateFiles(DataDirectory, "*.db")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => AppKey.IsValid(name))
            .Select(name => name!);

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

    private async Task<SqliteManager> ProvisionAppKeyAsync(string appKey)
    {
        var db = new SqliteManager(BuildConnectionString(PathFor(appKey)));

        await EnableWalAsync(db);
        await db.CreateTableAsync(Schema.CreateKeyValTable);
        await db.ExecuteAsync(Schema.CreateKeyValIndex);

        _logger.LogDebug("Provisioned database for app key {AppKey}", appKey);
        return db;
    }

    private async Task<SqliteManager> ProvisionCatalogAsync()
    {
        var db = new SqliteManager(BuildConnectionString(Path.Combine(DataDirectory, Schema.CatalogFileName)));

        await EnableWalAsync(db);
        await db.CreateTableAsync(Schema.CreateAppKeyTable);

        return db;
    }

    /// <summary>
    /// Write-ahead logging lets readers keep working while a write is in flight, which is what
    /// makes a file-per-tenant design hold up under concurrent traffic. The setting is persisted
    /// inside the database file, so applying it once at provisioning time is enough.
    /// </summary>
    private static Task EnableWalAsync(SqliteManager db) => db.ExecuteAsync("PRAGMA journal_mode=WAL;");

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
