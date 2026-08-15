namespace Immanuel.KeyValue.Core;

/// <summary>
/// All SQL that defines the shape of the data. Kept in one place so the web app, the tests and
/// the migration tool can never disagree about what a database looks like.
/// </summary>
public static class Schema
{
    /// <summary>
    /// Timestamps are stored as ISO-8601 UTC text ("2026-08-15T09:30:00Z"). Text keeps them
    /// readable when you open the file in any SQLite browser, and the trailing Z means nobody
    /// has to guess the time zone.
    /// </summary>
    public const string UtcNow = "strftime('%Y-%m-%dT%H:%M:%SZ','now')";

    /// <summary>The catalog lives in its own file. "_catalog" cannot collide with an app key,
    /// because app keys are always exactly 8 characters of [a-z0-9] and never contain "_".</summary>
    public const string CatalogFileName = "_catalog.db";

    /// <summary>One row per key, in the app key's own database. The old ClientKey column is gone:
    /// the file name is the client key now.</summary>
    public const string CreateKeyValTable = $"""
        CREATE TABLE IF NOT EXISTS KeyVal (
            Id        INTEGER PRIMARY KEY AUTOINCREMENT,
            KeyName   TEXT    NOT NULL,
            KeyVal    TEXT    NULL,
            IpAddr    TEXT    NULL,
            Agent     TEXT    NULL,
            CreatedAt TEXT    NOT NULL DEFAULT ({UtcNow}),
            UpdatedAt TEXT    NOT NULL DEFAULT ({UtcNow})
        );
        """;

    /// <summary>Carries over v1's IX_ClientKey_KeyName uniqueness, and makes the upsert in
    /// <see cref="KeyValueStore"/> possible by giving ON CONFLICT a target.</summary>
    public const string CreateKeyValIndex =
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_KeyVal_KeyName ON KeyVal (KeyName);";

    /// <summary>
    /// The registry of issued app keys. KeyCount is a cached total so /GetCount stays a single
    /// fast query instead of opening every tenant database.
    /// </summary>
    public const string CreateAppKeyTable = $"""
        CREATE TABLE IF NOT EXISTS AppKey (
            ClientKey    TEXT    NOT NULL PRIMARY KEY,
            KeyCount     INTEGER NOT NULL DEFAULT 0,
            IpAddr       TEXT    NULL,
            Agent        TEXT    NULL,
            CreatedAt    TEXT    NOT NULL DEFAULT ({UtcNow}),
            LastAccessAt TEXT    NULL
        );
        """;
}
