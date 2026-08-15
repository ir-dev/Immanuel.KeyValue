using System.Collections.Concurrent;
using Ark.Rapid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Immanuel.KeyValue.Core;

/// <summary>
/// Every key-value operation the service offers. Each app key gets its own SQLite database, so
/// one user's traffic never contends with another's, and the catalog is only touched when
/// something actually changes.
/// </summary>
public sealed class KeyValueStore(
    SqliteStoreFactory factory,
    AppKeyCatalog catalog,
    IOptions<KeyValueOptions> options,
    ILogger<KeyValueStore> logger)
{
    private readonly KeyValueOptions _options = options.Value;

    // Stamping LastAccessAt on every request would funnel all traffic through the one catalog
    // file and undo the per-app-key isolation. Stamping at most once per app key per interval
    // keeps the field useful for spotting dormant keys at a fraction of the write cost.
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastTouched = new();

    /// <summary>
    /// Issues a new app key and provisions its database.
    /// </summary>
    /// <param name="owner">
    /// The account the key belongs to, or null to issue an anonymous key. An owned key's database
    /// is created inside that account's folder - <c>App_Data/raj_at_immanuel.co/</c> - and only
    /// that account may read or write it.
    /// </param>
    public async Task<string> CreateAppKeyAsync(string? ipAddress, string? userAgent, UserAccount? owner = null)
    {
        // Retry only guards against the vanishingly unlikely collision of two identical
        // random keys; 36^8 is roughly 2.8 trillion combinations.
        for (var attempt = 0; ; attempt++)
        {
            var appKey = AppKey.Generate();
            if (factory.Exists(appKey))
            {
                if (attempt < 5) continue;
                throw new InvalidOperationException("Could not allocate a free app key.");
            }

            await factory.CreateAsync(appKey, owner?.Folder);
            await catalog.RegisterIfMissingAsync(appKey, ipAddress, userAgent, owner?.Email);

            logger.LogInformation("Issued app key {AppKey} to {Owner}", appKey, owner?.Email ?? "anonymous");
            return appKey;
        }
    }

    /// <summary>True when this app key has been issued. Answered from the file system, so it
    /// costs nothing and never queues behind the catalog.</summary>
    public bool AppKeyExists(string appKey) => factory.Exists(appKey);

    /// <summary>
    /// The account owning an app key, or null when it is anonymous or unknown. Read from where
    /// the file sits rather than from the catalog, so authorising a request stays a file-system
    /// lookup and never queues behind the one shared catalog database.
    /// </summary>
    public string? OwnerOf(string appKey) => factory.OwnerOf(appKey);

    /// <summary>Every app key belonging to one account.</summary>
    public Task<IReadOnlyList<AppKeyInfo>> ListAppKeysAsync(string ownerEmail) =>
        catalog.ListByOwnerAsync(ownerEmail);

    public Task<long> CountAppKeysAsync(string ownerEmail) => catalog.CountByOwnerAsync(ownerEmail);

    /// <summary>
    /// Deletes an app key and everything stored under it. Irreversible, which is why only the
    /// account that owns the key can reach this.
    /// </summary>
    public async Task<bool> DeleteAppKeyAsync(string appKey)
    {
        if (!AppKey.IsValid(appKey) || !factory.Exists(appKey)) return false;

        var deleted = factory.Delete(appKey);
        if (deleted) await catalog.DeleteAsync(appKey);

        _lastTouched.TryRemove(appKey, out _);

        logger.LogInformation("Deleted app key {AppKey}", appKey);
        return deleted;
    }

    public Task<AppKeyInfo?> GetAppKeyInfoAsync(string appKey) =>
        AppKey.IsValid(appKey) ? catalog.GetAsync(appKey) : Task.FromResult<AppKeyInfo?>(null);

    public Task<StoreStats> GetStatsAsync() => catalog.GetStatsAsync();

    /// <summary>Reads one value. Returns null when the app key or the key does not exist.</summary>
    public async Task<string?> GetValueAsync(string appKey, string key)
    {
        var db = await ResolveAsync(appKey, forWrite: false);
        if (db is null || string.IsNullOrEmpty(key)) return null;

        var entry = await db.FirstAsync<KeyValueEntry?>(
            $"SELECT KeyName, KeyVal, CreatedAt, UpdatedAt FROM KeyVal WHERE KeyName = {await db.GetSqlValueAsync(key)};");

        return entry?.KeyVal;
    }

    /// <summary>Reads one key with its timestamps.</summary>
    public async Task<KeyValueEntry?> GetEntryAsync(string appKey, string key)
    {
        var db = await ResolveAsync(appKey, forWrite: false);
        if (db is null || string.IsNullOrEmpty(key)) return null;

        return await db.FirstAsync<KeyValueEntry?>(
            $"SELECT KeyName, KeyVal, CreatedAt, UpdatedAt FROM KeyVal WHERE KeyName = {await db.GetSqlValueAsync(key)};");
    }

    /// <summary>Every key stored under one app key. Null when the app key does not exist.</summary>
    public async Task<IReadOnlyList<KeyValueEntry>?> ListAsync(string appKey)
    {
        var db = await ResolveAsync(appKey, forWrite: false);
        if (db is null) return null;

        var rows = await db.ExecuteSelectAsync<KeyValueEntry>(
            "SELECT KeyName, KeyVal, CreatedAt, UpdatedAt FROM KeyVal ORDER BY KeyName;");

        return rows.ToList();
    }

    /// <summary>Creates or overwrites a key. This is v1's sp_UpdateKeyVal.</summary>
    public async Task<SetResult> SetValueAsync(
        string appKey, string key, string? value, string? ipAddress, string? userAgent)
    {
        if (!AppKey.IsValid(appKey)) return new SetResult(StoreStatus.Invalid, false);
        if (!IsKeyAcceptable(key)) return new SetResult(StoreStatus.Invalid, false);
        if (value is not null && value.Length > _options.MaxValueLength)
        {
            return new SetResult(StoreStatus.Invalid, false);
        }

        var db = await ResolveAsync(appKey, forWrite: true);
        if (db is null) return new SetResult(StoreStatus.AppKeyNotFound, false);

        var keyLiteral = await db.GetSqlValueAsync(key);
        var existed = await db.ExecuteCountAsync($"SELECT COUNT(*) FROM KeyVal WHERE KeyName = {keyLiteral};") > 0;

        if (!existed)
        {
            var used = await db.ExecuteCountAsync("SELECT COUNT(*) FROM KeyVal;");
            if (used >= _options.MaxKeysPerAppKey) return new SetResult(StoreStatus.KeyLimitReached, false);
        }

        // v1 used a T-SQL MERGE for this. SQLite's ON CONFLICT does the same job in a single
        // statement, so two writers racing on the same key cannot produce a duplicate row.
        // IpAddr/Agent are refreshed on update as well, so they name the most recent writer.
        await db.ExecuteAsync($"""
            INSERT INTO KeyVal (KeyName, KeyVal, IpAddr, Agent)
            VALUES (
                {keyLiteral},
                {await db.GetSqlValueAsync(Sql.OrNull(value))},
                {await db.GetSqlValueAsync(Sql.OrNull(ipAddress))},
                {await db.GetSqlValueAsync(Sql.OrNull(userAgent))})
            ON CONFLICT(KeyName) DO UPDATE SET
                KeyVal    = excluded.KeyVal,
                IpAddr    = excluded.IpAddr,
                Agent     = excluded.Agent,
                UpdatedAt = {Schema.UtcNow};
            """);

        if (!existed) await RefreshKeyCountAsync(appKey, db);
        await TouchQuietlyAsync(appKey);

        return new SetResult(StoreStatus.Ok, !existed);
    }

    /// <summary>Removes one key. New in v2 - v1 had no way to delete anything.</summary>
    public async Task<StoreStatus> DeleteAsync(string appKey, string key)
    {
        if (!AppKey.IsValid(appKey)) return StoreStatus.Invalid;
        if (!IsKeyAcceptable(key)) return StoreStatus.Invalid;

        var db = await ResolveAsync(appKey, forWrite: false);
        if (db is null) return StoreStatus.AppKeyNotFound;

        var keyLiteral = await db.GetSqlValueAsync(key);
        var existed = await db.ExecuteCountAsync($"SELECT COUNT(*) FROM KeyVal WHERE KeyName = {keyLiteral};") > 0;
        if (!existed) return StoreStatus.KeyNotFound;

        await db.ExecuteAsync($"DELETE FROM KeyVal WHERE KeyName = {keyLiteral};");
        await RefreshKeyCountAsync(appKey, db);
        await TouchQuietlyAsync(appKey);

        return StoreStatus.Ok;
    }

    /// <summary>
    /// Adds <paramref name="by"/> to a numeric value (negative to decrement). This is v1's
    /// sp_UpdateAction, which only ever supported increment by one.
    /// </summary>
    /// <param name="createIfMissing">
    /// Start the counter at zero when the key does not exist yet. v1 silently did nothing here,
    /// which meant a counter never started unless you remembered to seed it with a separate
    /// write first; counting from zero is what callers almost always wanted.
    /// </param>
    public async Task<AdjustResult> AdjustAsync(string appKey, string key, long by, bool createIfMissing = true)
    {
        if (!AppKey.IsValid(appKey)) return new AdjustResult(StoreStatus.Invalid, null);
        if (!IsKeyAcceptable(key)) return new AdjustResult(StoreStatus.Invalid, null);

        var db = await ResolveAsync(appKey, forWrite: true);
        if (db is null) return new AdjustResult(StoreStatus.AppKeyNotFound, null);

        var keyLiteral = await db.GetSqlValueAsync(key);
        var existed = await db.ExecuteCountAsync($"SELECT COUNT(*) FROM KeyVal WHERE KeyName = {keyLiteral};") > 0;

        if (!existed)
        {
            if (!createIfMissing) return new AdjustResult(StoreStatus.KeyNotFound, null);

            var used = await db.ExecuteCountAsync("SELECT COUNT(*) FROM KeyVal;");
            if (used >= _options.MaxKeysPerAppKey) return new AdjustResult(StoreStatus.KeyLimitReached, null);

            // DO NOTHING rather than DO UPDATE: if another request seeded the counter a moment
            // ago, its value stands and the UPDATE below adds to it.
            await db.ExecuteAsync(
                $"INSERT INTO KeyVal (KeyName, KeyVal) VALUES ({keyLiteral}, '0') ON CONFLICT(KeyName) DO NOTHING;");

            await RefreshKeyCountAsync(appKey, db);
        }

        // Treat NULL and empty as zero, the way v1's ISNULL([KeyVal], 0) did.
        const string current = "COALESCE(NULLIF(KeyVal, ''), '0')";

        // A single statement, so simultaneous counters cannot lose an update the way a
        // read-modify-write round trip would. The numeric guard means a non-numeric value
        // matches no rows rather than being overwritten with garbage.
        await db.ExecuteAsync($"""
            UPDATE KeyVal
               SET KeyVal    = CAST(CAST({current} AS INTEGER) + ({by}) AS TEXT),
                   UpdatedAt = {Schema.UtcNow}
             WHERE KeyName = {keyLiteral}
               AND {Sql.IsWholeNumber(current)};
            """);

        await TouchQuietlyAsync(appKey);

        // Read back so we can tell "no such key" apart from "the guard refused it".
        var entry = await db.FirstAsync<KeyValueEntry?>(
            $"SELECT KeyName, KeyVal, CreatedAt, UpdatedAt FROM KeyVal WHERE KeyName = {keyLiteral};");

        if (entry is null) return new AdjustResult(StoreStatus.KeyNotFound, null);

        return long.TryParse(entry.KeyVal, out _)
            ? new AdjustResult(StoreStatus.Ok, entry.KeyVal)
            : new AdjustResult(StoreStatus.NotNumeric, entry.KeyVal);
    }

    private bool IsKeyAcceptable(string key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= _options.MaxKeyLength;

    /// <summary>
    /// Maps an app key onto its database. Returns null when the app key is malformed or was
    /// never issued - only a write, and only with <see cref="KeyValueOptions.AutoCreateUnknownAppKeys"/>
    /// turned on, will bring a new database into existence.
    /// </summary>
    private async Task<SqliteManager?> ResolveAsync(string appKey, bool forWrite)
    {
        if (!AppKey.IsValid(appKey)) return null;

        if (!factory.Exists(appKey))
        {
            if (!forWrite || !_options.AutoCreateUnknownAppKeys) return null;

            var db = await factory.OpenAsync(appKey);
            await catalog.RegisterIfMissingAsync(appKey, null, null);
            return db;
        }

        return await factory.OpenAsync(appKey);
    }

    /// <summary>Recomputes one app key's cached key count. Only called when a key is added or
    /// removed, so the catalog stays quiet during ordinary reads and overwrites.</summary>
    private async Task RefreshKeyCountAsync(string appKey, SqliteManager db)
    {
        try
        {
            var count = await db.ExecuteCountAsync("SELECT COUNT(*) FROM KeyVal;");
            await catalog.SetKeyCountAsync(appKey, count);
        }
        catch (Exception ex)
        {
            // The user's write already succeeded; a stale total is not worth failing it over.
            logger.LogWarning(ex, "Could not refresh the key count for app key {AppKey}", appKey);
        }
    }

    private async Task TouchQuietlyAsync(string appKey)
    {
        var now = DateTimeOffset.UtcNow;
        var previous = _lastTouched.GetOrAdd(appKey, DateTimeOffset.MinValue);

        if (now - previous < TouchInterval) return;

        // Whoever wins this swap does the write; everyone else moves on.
        if (!_lastTouched.TryUpdate(appKey, now, previous)) return;

        try
        {
            await catalog.TouchAsync(appKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not stamp last access for app key {AppKey}", appKey);
        }
    }
}
