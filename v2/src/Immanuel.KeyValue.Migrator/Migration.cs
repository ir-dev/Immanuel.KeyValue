using Ark.Rapid;
using Immanuel.KeyValue.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Immanuel.KeyValue.Migrator;

/// <summary>
/// Reads every row of the v1 KeyVal table and fans it out into one SQLite database per app key,
/// the layout v2 expects. Reuses <see cref="Schema"/> and <see cref="AppKey"/> from the Core
/// project so the files it produces cannot drift from what the web app reads.
/// </summary>
public sealed class Migration(MigratorOptions options)
{
    private readonly SqliteStoreFactory _factory = new(
        Options.Create(new KeyValueOptions { DataDirectory = options.DataDirectory }),
        Directory.GetCurrentDirectory(),
        NullLogger<SqliteStoreFactory>.Instance);

    public async Task<MigrationReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var report = new MigrationReport();
        var rows = await ReadSourceRowsAsync(cancellationToken);

        report.RowsRead = rows.Count;
        Console.WriteLine($"Read {rows.Count:N0} rows from {options.Table}.");

        // Group first so each app key's database is opened once rather than per row.
        var groups = new Dictionary<string, List<SourceRow>>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var appKey = ResolveAppKey(row.ClientKey, report);
            if (appKey is null) continue;

            if (!groups.TryGetValue(appKey, out var list))
            {
                groups[appKey] = list = [];
            }

            list.Add(row);
        }

        Console.WriteLine($"Mapped to {groups.Count:N0} app keys.");

        if (options.DryRun)
        {
            Console.WriteLine("Dry run - nothing written.");
            report.AppKeys = groups.Count;
            report.KeysWritten = groups.Sum(g => g.Value.Count);
            return report;
        }

        var catalog = new AppKeyCatalog(_factory);

        foreach (var (appKey, group) in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var db = await _factory.OpenAsync(appKey);
            await catalog.RegisterIfMissingAsync(appKey, group[0].IpAddr, group[0].Agent);

            foreach (var row in group)
            {
                if (await WriteRowAsync(db, row)) report.KeysWritten++;
                else report.KeysSkipped++;
            }

            // Keep the catalog's cached total honest for /GetCount and /api/v2/stats.
            await catalog.SetKeyCountAsync(appKey, await db.ExecuteCountAsync("SELECT COUNT(*) FROM KeyVal;"));

            report.AppKeys++;
            if (report.AppKeys % 50 == 0) Console.WriteLine($"  ... {report.AppKeys:N0} app keys done");
        }

        return report;
    }

    /// <summary>
    /// Writes one row, preserving the original CreatedAt where v1 recorded one. Returns false
    /// when the key already existed and --overwrite was not given.
    /// </summary>
    private async Task<bool> WriteRowAsync(SqliteManager db, SourceRow row)
    {
        var keyLiteral = await db.GetSqlValueAsync(row.KeyName);

        if (!options.Overwrite)
        {
            var exists = await db.ExecuteCountAsync($"SELECT COUNT(*) FROM KeyVal WHERE KeyName = {keyLiteral};") > 0;
            if (exists) return false;
        }

        // CreatedAt falls back to now when v1 never had the column; UpdatedAt tracks it, since
        // v1 stored no modification time at all.
        var createdAt = row.CreatedAt is { } stamp
            ? await db.GetSqlValueAsync(stamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))
            : Schema.UtcNow;

        await db.ExecuteAsync($"""
            INSERT INTO KeyVal (KeyName, KeyVal, IpAddr, Agent, CreatedAt, UpdatedAt)
            VALUES (
                {keyLiteral},
                {await db.GetSqlValueAsync(Sql.OrNull(row.KeyVal))},
                {await db.GetSqlValueAsync(Sql.OrNull(row.IpAddr))},
                {await db.GetSqlValueAsync(Sql.OrNull(row.Agent))},
                {createdAt},
                {createdAt})
            ON CONFLICT(KeyName) DO UPDATE SET
                KeyVal    = excluded.KeyVal,
                IpAddr    = excluded.IpAddr,
                Agent     = excluded.Agent,
                UpdatedAt = excluded.UpdatedAt;
            """);

        return true;
    }

    /// <summary>
    /// v1's ClientKey was a bare varchar(8) with no validation, so a handful of rows can hold
    /// something that is not a legal v2 app key. Lowercase what can be salvaged, report the rest.
    /// </summary>
    private string? ResolveAppKey(string clientKey, MigrationReport report)
    {
        if (AppKey.IsValid(clientKey)) return clientKey;

        if (options.NormalizeCase)
        {
            var lowered = clientKey.ToLowerInvariant();
            if (AppKey.IsValid(lowered))
            {
                report.Normalized.Add(clientKey);
                return lowered;
            }
        }

        report.Unmigratable.Add(clientKey);
        return null;
    }

    private async Task<List<SourceRow>> ReadSourceRowsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.Source);
        await connection.OpenAsync(cancellationToken);

        var hasCreatedAt = await HasCreatedAtColumnAsync(connection, options.Table, cancellationToken);
        if (!hasCreatedAt)
        {
            Console.WriteLine("Source has no CreatedAt column - timestamps will be set to the migration time.");
        }

        var createdAt = hasCreatedAt ? "CreatedAt" : "NULL AS CreatedAt";

        // options.Table is an operator-supplied identifier, not user input, so it is
        // interpolated. Everything read out of it is handled as data.
        await using var command = new SqlCommand(
            $"SELECT ClientKey, KeyName, KeyVal, IpAddr, Agent, {createdAt} FROM {options.Table};", connection);
        command.CommandTimeout = 0;

        var rows = new List<SourceRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SourceRow(
                ClientKey: reader.GetString(0),
                KeyName: reader.GetString(1),
                KeyVal: reader.IsDBNull(2) ? null : reader.GetString(2),
                IpAddr: reader.IsDBNull(3) ? null : reader.GetString(3),
                Agent: reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedAt: reader.IsDBNull(5) ? null : reader.GetDateTime(5)));
        }

        return rows;
    }

    /// <summary>
    /// v1 shipped CreatedAt as a commented-out ALTER, so some deployments have it and some
    /// do not. Ask rather than assume.
    /// </summary>
    private static async Task<bool> HasCreatedAtColumnAsync(
        SqlConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(@table) AND name = 'CreatedAt';",
            connection);

        command.Parameters.AddWithValue("@table", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private sealed record SourceRow(
        string ClientKey, string KeyName, string? KeyVal, string? IpAddr, string? Agent, DateTime? CreatedAt);
}

public sealed class MigrationReport
{
    public int RowsRead { get; set; }
    public int AppKeys { get; set; }
    public int KeysWritten { get; set; }
    public int KeysSkipped { get; set; }

    /// <summary>Client keys that had to be lowercased to become valid app keys.</summary>
    public HashSet<string> Normalized { get; } = new(StringComparer.Ordinal);

    /// <summary>Client keys that are not valid app keys at all - these rows were left behind.</summary>
    public HashSet<string> Unmigratable { get; } = new(StringComparer.Ordinal);
}
