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

    /// <summary>Accounts, one-time passwords and sessions. Underscore-prefixed for the same
    /// reason as the catalog, and kept separate so sign-in traffic never queues behind it.</summary>
    public const string UsersFileName = "_users.db";

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
            LastAccessAt TEXT    NULL,
            OwnerEmail   TEXT    NULL
        );
        """;

    /// <summary>
    /// OwnerEmail arrived after the first release, so catalogs created by an earlier build are
    /// missing it and CREATE TABLE IF NOT EXISTS will not add it. SQLite has no
    /// "ADD COLUMN IF NOT EXISTS", hence the PRAGMA check in
    /// <see cref="SqliteStoreFactory"/> that decides whether to run this.
    /// </summary>
    public const string AddAppKeyOwnerColumn = "ALTER TABLE AppKey ADD COLUMN OwnerEmail TEXT NULL;";

    /// <summary>Finding every app key belonging to one account is a per-page query on the
    /// console, so it gets an index rather than a scan.</summary>
    public const string CreateAppKeyOwnerIndex =
        "CREATE INDEX IF NOT EXISTS IX_AppKey_OwnerEmail ON AppKey (OwnerEmail);";

    /// <summary>
    /// One row per signed-up account. The email is the primary key and is always stored
    /// lowercase; Folder is the derived directory name, kept alongside so nothing has to
    /// re-derive it while resolving a request.
    ///
    /// HeaderName/HeaderValue are the caller-chosen HTTP header that authenticates that
    /// account's API calls. They are stored as given because the console shows them back to
    /// the signed-in user - see the note in the README.
    /// </summary>
    public const string CreateUserTable = $"""
        CREATE TABLE IF NOT EXISTS User (
            Email       TEXT NOT NULL PRIMARY KEY,
            Folder      TEXT NOT NULL,
            HeaderName  TEXT NULL,
            HeaderValue TEXT NULL,
            CreatedAt   TEXT NOT NULL DEFAULT ({UtcNow}),
            VerifiedAt  TEXT NULL,
            LastLoginAt TEXT NULL
        );
        """;

    /// <summary>
    /// Two accounts presenting the same header name and value would make an API call ambiguous,
    /// so the pair is unique. SQLite treats NULLs as distinct in a unique index, which is what
    /// lets every account that has not configured a header keep its NULL pair.
    /// </summary>
    public const string CreateUserHeaderIndex =
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_User_Header ON User (HeaderName, HeaderValue);";

    /// <summary>
    /// The one-time password in flight for an account - at most one, because requesting a new
    /// code replaces the old. Only the hash is kept, so a leaked database file does not hand
    /// anyone a working code. Attempts is what stops a six-digit code being brute-forced.
    /// </summary>
    public const string CreateOtpTable = $"""
        CREATE TABLE IF NOT EXISTS Otp (
            Email     TEXT    NOT NULL PRIMARY KEY,
            CodeHash  TEXT    NOT NULL,
            ExpiresAt TEXT    NOT NULL,
            Attempts  INTEGER NOT NULL DEFAULT 0,
            CreatedAt TEXT    NOT NULL DEFAULT ({UtcNow})
        );
        """;

    /// <summary>Console sessions, keyed by the hash of the bearer token for the same reason
    /// the OTP is hashed.</summary>
    public const string CreateSessionTable = $"""
        CREATE TABLE IF NOT EXISTS Session (
            TokenHash TEXT NOT NULL PRIMARY KEY,
            Email     TEXT NOT NULL,
            CreatedAt TEXT NOT NULL DEFAULT ({UtcNow}),
            ExpiresAt TEXT NOT NULL
        );
        """;

    public const string CreateSessionEmailIndex =
        "CREATE INDEX IF NOT EXISTS IX_Session_Email ON Session (Email);";
}
