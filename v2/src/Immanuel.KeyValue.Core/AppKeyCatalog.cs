using Ark.Rapid;

namespace Immanuel.KeyValue.Core;

/// <summary>
/// The registry of issued app keys, kept in its own SQLite file alongside the per-app-key
/// databases. It answers "is this a real app key?" without touching the tenant files, and it
/// caches each app key's key count so service-wide totals stay a single query.
/// </summary>
public sealed class AppKeyCatalog(SqliteStoreFactory factory)
{
    /// <summary>Records a freshly issued app key.</summary>
    public async Task RegisterAsync(string appKey, string? ipAddress, string? userAgent)
    {
        var db = await factory.OpenCatalogAsync();

        await db.InsertTableAsync("AppKey", new Dictionary<string, object>
        {
            ["ClientKey"] = appKey,
            ["IpAddr"] = Sql.OrNull(ipAddress),
            ["Agent"] = Sql.OrNull(userAgent),
        });
    }

    /// <summary>Adds a catalog row for an app key whose database already exists but which was
    /// never registered - used when adopting v1 data and when auto-create is enabled.</summary>
    public async Task RegisterIfMissingAsync(string appKey, string? ipAddress, string? userAgent)
    {
        var db = await factory.OpenCatalogAsync();
        var key = await db.GetSqlValueAsync(appKey);

        await db.ExecuteAsync(
            $"INSERT INTO AppKey (ClientKey, IpAddr, Agent) " +
            $"VALUES ({key}, {await db.GetSqlValueAsync(Sql.OrNull(ipAddress))}, {await db.GetSqlValueAsync(Sql.OrNull(userAgent))}) " +
            $"ON CONFLICT(ClientKey) DO NOTHING;");
    }

    public async Task<bool> ExistsAsync(string appKey)
    {
        var db = await factory.OpenCatalogAsync();
        var key = await db.GetSqlValueAsync(appKey);

        return await db.ExecuteCountAsync($"SELECT COUNT(*) FROM AppKey WHERE ClientKey = {key};") > 0;
    }

    public async Task<AppKeyInfo?> GetAsync(string appKey)
    {
        var db = await factory.OpenCatalogAsync();
        var key = await db.GetSqlValueAsync(appKey);

        return await db.FirstAsync<AppKeyInfo?>(
            $"SELECT ClientKey, KeyCount, CreatedAt, LastAccessAt FROM AppKey WHERE ClientKey = {key};");
    }

    /// <summary>Stamps the app key as used. Fire-and-forget from the caller's point of view -
    /// a failure here must never fail the read or write the caller actually asked for.</summary>
    public async Task TouchAsync(string appKey)
    {
        var db = await factory.OpenCatalogAsync();
        var key = await db.GetSqlValueAsync(appKey);

        await db.ExecuteAsync($"UPDATE AppKey SET LastAccessAt = {Schema.UtcNow} WHERE ClientKey = {key};");
    }

    /// <summary>Refreshes the cached key count for one app key.</summary>
    public async Task SetKeyCountAsync(string appKey, long count)
    {
        var db = await factory.OpenCatalogAsync();

        await db.UpdateTableAsync(
            "AppKey",
            new Dictionary<string, object> { ["KeyCount"] = count },
            new Dictionary<string, object> { ["ClientKey"] = appKey });
    }

    public async Task DeleteAsync(string appKey)
    {
        var db = await factory.OpenCatalogAsync();
        var key = await db.GetSqlValueAsync(appKey);

        await db.ExecuteAsync($"DELETE FROM AppKey WHERE ClientKey = {key};");
    }

    /// <summary>
    /// Service-wide totals. Both come from the catalog, so this stays fast no matter how many
    /// app-key databases exist. IFNULL guards SUM() returning NULL on an empty catalog.
    /// </summary>
    public async Task<StoreStats> GetStatsAsync()
    {
        var db = await factory.OpenCatalogAsync();

        return new StoreStats
        {
            AppKeys = await db.ExecuteCountAsync("SELECT COUNT(*) FROM AppKey;"),
            Keys = await db.ExecuteCountAsync("SELECT IFNULL(SUM(KeyCount), 0) FROM AppKey;"),
        };
    }
}
