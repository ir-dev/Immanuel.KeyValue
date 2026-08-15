using Ark.Rapid;

namespace Immanuel.KeyValue.Core;

/// <summary>
/// Everything stored about accounts: the users themselves, the one-time password each has in
/// flight, and their console sessions. It all lives in <c>_users.db</c>, one file next to the
/// app-key catalog, so sign-in traffic never contends with anybody's key-value reads.
///
/// This class is data access only - what the rules are lives in <see cref="AccountService"/>.
/// </summary>
public sealed class UserDirectory(SqliteStoreFactory factory)
{
    private const string UserColumns = "Email, Folder, HeaderName, HeaderValue, CreatedAt, VerifiedAt, LastLoginAt";

    // ---------- users ----------

    public async Task<UserAccount?> FindAsync(string email)
    {
        var db = await factory.OpenUsersAsync();

        return await db.FirstAsync<UserAccount?>(
            $"SELECT {UserColumns} FROM User WHERE Email = {await db.GetSqlValueAsync(email)};");
    }

    /// <summary>
    /// Creates the account unless it exists already. ON CONFLICT rather than a check-then-insert
    /// so two sign-ups racing on the same address cannot both get past it.
    /// </summary>
    public async Task CreateIfMissingAsync(string email, string folder)
    {
        var db = await factory.OpenUsersAsync();

        await db.ExecuteAsync(
            $"INSERT INTO User (Email, Folder) " +
            $"VALUES ({await db.GetSqlValueAsync(email)}, {await db.GetSqlValueAsync(folder)}) " +
            $"ON CONFLICT(Email) DO NOTHING;");
    }

    /// <summary>Stamps a successful sign-in, and the first one as the address being proved.</summary>
    public async Task MarkSignedInAsync(string email)
    {
        var db = await factory.OpenUsersAsync();

        await db.ExecuteAsync($"""
            UPDATE User
               SET LastLoginAt = {Schema.UtcNow},
                   VerifiedAt  = COALESCE(VerifiedAt, {Schema.UtcNow})
             WHERE Email = {await db.GetSqlValueAsync(email)};
            """);
    }

    /// <summary>Sets or clears the account's custom API header. Both values move together, so
    /// a half-configured header can never exist.</summary>
    public async Task SetHeaderAsync(string email, string? name, string? value)
    {
        var db = await factory.OpenUsersAsync();

        await db.ExecuteAsync($"""
            UPDATE User
               SET HeaderName  = {await db.GetSqlValueAsync(Sql.OrNull(name))},
                   HeaderValue = {await db.GetSqlValueAsync(Sql.OrNull(value))}
             WHERE Email = {await db.GetSqlValueAsync(email)};
            """);
    }

    /// <summary>The account presenting this header, or null. This is the lookup on the API path,
    /// and it is why (HeaderName, HeaderValue) carries a unique index.</summary>
    public async Task<UserAccount?> FindByHeaderAsync(string name, string value)
    {
        var db = await factory.OpenUsersAsync();

        return await db.FirstAsync<UserAccount?>($"""
            SELECT {UserColumns} FROM User
             WHERE HeaderName = {await db.GetSqlValueAsync(name)}
               AND HeaderValue = {await db.GetSqlValueAsync(value)};
            """);
    }

    /// <summary>
    /// Every header name any account has claimed. The resolver caches this so an incoming request
    /// can be checked against a handful of candidate headers instead of one query per header sent.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListHeaderNamesAsync()
    {
        var db = await factory.OpenUsersAsync();

        var rows = await db.ExecuteSelectAsync<HeaderNameRow>(
            "SELECT DISTINCT HeaderName FROM User WHERE HeaderName IS NOT NULL;");

        return rows.Select(row => row.HeaderName).Where(name => !string.IsNullOrEmpty(name)).ToList();
    }

    // ---------- one-time passwords ----------

    /// <summary>Replaces whatever code was outstanding for this address with a new one.</summary>
    public async Task StoreOtpAsync(string email, string codeHash, DateTimeOffset expiresAt)
    {
        var db = await factory.OpenUsersAsync();

        await db.ExecuteAsync($"""
            INSERT INTO Otp (Email, CodeHash, ExpiresAt, Attempts)
            VALUES (
                {await db.GetSqlValueAsync(email)},
                {await db.GetSqlValueAsync(codeHash)},
                {await db.GetSqlValueAsync(Timestamps.Format(expiresAt))},
                0)
            ON CONFLICT(Email) DO UPDATE SET
                CodeHash  = excluded.CodeHash,
                ExpiresAt = excluded.ExpiresAt,
                Attempts  = 0,
                CreatedAt = {Schema.UtcNow};
            """);
    }

    public async Task<OtpRecord?> FindOtpAsync(string email)
    {
        var db = await factory.OpenUsersAsync();

        return await db.FirstAsync<OtpRecord?>(
            $"SELECT Email, CodeHash, ExpiresAt, Attempts FROM Otp WHERE Email = {await db.GetSqlValueAsync(email)};");
    }

    /// <summary>Counts a wrong guess and returns the new total, so the caller can decide whether
    /// the limit has been spent.</summary>
    public async Task<long> RecordFailedAttemptAsync(string email)
    {
        var db = await factory.OpenUsersAsync();
        var key = await db.GetSqlValueAsync(email);

        await db.ExecuteAsync($"UPDATE Otp SET Attempts = Attempts + 1 WHERE Email = {key};");

        return await db.ExecuteCountAsync($"SELECT IFNULL(MAX(Attempts), 0) FROM Otp WHERE Email = {key};");
    }

    public async Task DeleteOtpAsync(string email)
    {
        var db = await factory.OpenUsersAsync();

        await db.ExecuteAsync($"DELETE FROM Otp WHERE Email = {await db.GetSqlValueAsync(email)};");
    }

    // ---------- sessions ----------

    public async Task CreateSessionAsync(string tokenHash, string email, DateTimeOffset expiresAt)
    {
        var db = await factory.OpenUsersAsync();

        await db.InsertTableAsync("Session", new Dictionary<string, object>
        {
            ["TokenHash"] = tokenHash,
            ["Email"] = email,
            ["ExpiresAt"] = Timestamps.Format(expiresAt),
        });
    }

    /// <summary>The account behind a session token, provided the session has not expired.</summary>
    public async Task<UserAccount?> FindBySessionAsync(string tokenHash)
    {
        var db = await factory.OpenUsersAsync();

        return await db.FirstAsync<UserAccount?>($"""
            SELECT u.Email, u.Folder, u.HeaderName, u.HeaderValue, u.CreatedAt, u.VerifiedAt, u.LastLoginAt
              FROM Session s
              JOIN User u ON u.Email = s.Email
             WHERE s.TokenHash = {await db.GetSqlValueAsync(tokenHash)}
               AND s.ExpiresAt > {Schema.UtcNow};
            """);
    }

    public async Task DeleteSessionAsync(string tokenHash)
    {
        var db = await factory.OpenUsersAsync();

        await db.ExecuteAsync($"DELETE FROM Session WHERE TokenHash = {await db.GetSqlValueAsync(tokenHash)};");
    }

    /// <summary>Drops expired sessions and codes. Cheap, and called on sign-in rather than from a
    /// timer so there is nothing extra to keep running.</summary>
    public async Task PurgeExpiredAsync()
    {
        var db = await factory.OpenUsersAsync();

        await db.ExecuteAsync($"DELETE FROM Session WHERE ExpiresAt <= {Schema.UtcNow};");
        await db.ExecuteAsync($"DELETE FROM Otp WHERE ExpiresAt <= {Schema.UtcNow};");
    }

    /// <summary>A row of the DISTINCT HeaderName query.</summary>
    private sealed class HeaderNameRow
    {
        public string HeaderName { get; set; } = "";
    }
}

/// <summary>The one-time password outstanding for an address.</summary>
public sealed class OtpRecord
{
    public string Email { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public string ExpiresAt { get; set; } = "";
    public long Attempts { get; set; }
}
