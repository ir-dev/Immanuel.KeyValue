using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Immanuel.KeyValue.Core;

/// <summary>
/// Sign-up, sign-in and the credentials an account uses afterwards.
///
/// Signing in is deliberately passwordless: the address gets a six-digit code, and proving you
/// can read that address is the whole of the authentication. When no SMTP relay is configured
/// there is no way to deliver one, so the master code from appsettings is accepted instead -
/// which makes the service usable out of the box and is why the README is blunt about
/// configuring SMTP before exposing it.
/// </summary>
public sealed class AccountService(
    UserDirectory users,
    IOtpSender sender,
    IOptions<AuthOptions> options,
    ILogger<AccountService> logger)
{
    private readonly AuthOptions _auth = options.Value;

    // The set of header names some account has claimed, so an incoming request can be matched
    // against a handful of candidates rather than one query per header it happens to carry.
    // Rebuilt whenever a header changes and, as a backstop, once a minute.
    private static readonly TimeSpan HeaderNameCacheLifetime = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _headerNamesGate = new(1, 1);
    private IReadOnlyList<string> _headerNames = [];
    private DateTimeOffset _headerNamesLoadedAt = DateTimeOffset.MinValue;

    public bool Enabled => _auth.Enabled;

    /// <summary>Whether codes are really emailed. False means the master code is in play.</summary>
    public bool CanEmail => sender.CanSend;

    // ---------- sign-up and sign-in ----------

    /// <summary>
    /// Creates the account and sends its first code. An address that already has an account is
    /// refused here rather than silently turned into a sign-in, so the console can say which of
    /// the two the user wanted.
    /// </summary>
    public async Task<OtpRequestResult> SignUpAsync(string? email, CancellationToken cancellationToken = default)
    {
        var normalized = UserFolder.NormalizeEmail(email);
        if (normalized is null) return Invalid();

        var folder = UserFolder.ToFolderName(normalized);
        if (folder is null) return Invalid();

        if (await users.FindAsync(normalized) is not null)
        {
            return new OtpRequestResult(
                OtpRequestStatus.AlreadyRegistered, Delivery,
                Detail: "That address already has an account. Sign in instead.");
        }

        await users.CreateIfMissingAsync(normalized, folder);
        logger.LogInformation("Registered account {Email} in folder {Folder}", normalized, folder);

        return await IssueCodeAsync(normalized, cancellationToken);
    }

    /// <summary>Sends a code to an address that already has an account.</summary>
    public async Task<OtpRequestResult> SignInAsync(string? email, CancellationToken cancellationToken = default)
    {
        var normalized = UserFolder.NormalizeEmail(email);
        if (normalized is null) return Invalid();

        if (await users.FindAsync(normalized) is null)
        {
            return new OtpRequestResult(
                OtpRequestStatus.UnknownAccount, Delivery,
                Detail: "No account for that address yet. Sign up first.");
        }

        return await IssueCodeAsync(normalized, cancellationToken);
    }

    /// <summary>
    /// Checks a code and, on success, starts a console session. The code is consumed either way
    /// it ends - correctly, or by spending the last attempt - so a six-digit secret is never
    /// left sitting there to be guessed at leisure.
    /// </summary>
    public async Task<OtpVerifyResult> VerifyAsync(string? email, string? code)
    {
        var normalized = UserFolder.NormalizeEmail(email);
        if (normalized is null || string.IsNullOrWhiteSpace(code))
        {
            return new OtpVerifyResult(OtpVerifyStatus.InvalidEmail);
        }

        var outstanding = await users.FindOtpAsync(normalized);
        if (outstanding is null) return new OtpVerifyResult(OtpVerifyStatus.NoCode);

        if (Timestamps.Parse(outstanding.ExpiresAt) is not { } expiresAt || expiresAt <= DateTimeOffset.UtcNow)
        {
            await users.DeleteOtpAsync(normalized);
            return new OtpVerifyResult(OtpVerifyStatus.Expired);
        }

        if (!HashMatches(outstanding.CodeHash, HashCode(normalized, code.Trim())))
        {
            var attempts = await users.RecordFailedAttemptAsync(normalized);

            if (attempts >= _auth.OtpMaxAttempts)
            {
                await users.DeleteOtpAsync(normalized);
                logger.LogWarning("Discarded the sign-in code for {Email} after {Attempts} wrong guesses",
                    normalized, attempts);

                return new OtpVerifyResult(OtpVerifyStatus.TooManyAttempts);
            }

            return new OtpVerifyResult(OtpVerifyStatus.Incorrect);
        }

        await users.DeleteOtpAsync(normalized);
        await users.MarkSignedInAsync(normalized);

        var account = await users.FindAsync(normalized);
        if (account is null) return new OtpVerifyResult(OtpVerifyStatus.NoCode);

        var token = NewToken();
        var expires = DateTimeOffset.UtcNow + _auth.SessionLifetime;
        await users.CreateSessionAsync(HashToken(token), normalized, expires);

        logger.LogInformation("Signed in {Email}", normalized);
        return new OtpVerifyResult(OtpVerifyStatus.Ok, token, expires, account);
    }

    // ---------- sessions ----------

    public Task<UserAccount?> ResolveSessionAsync(string? token) =>
        string.IsNullOrWhiteSpace(token)
            ? Task.FromResult<UserAccount?>(null)
            : users.FindBySessionAsync(HashToken(token.Trim()));

    public Task SignOutAsync(string? token) =>
        string.IsNullOrWhiteSpace(token) ? Task.CompletedTask : users.DeleteSessionAsync(HashToken(token.Trim()));

    // ---------- the custom API header ----------

    /// <summary>
    /// Sets the HTTP header this account authenticates its API calls with. The name is stored
    /// lowercased because HTTP header names are case-insensitive and the lookup has to be too.
    /// </summary>
    public async Task<HeaderStatus> SetHeaderAsync(string email, string? name, string? value)
    {
        var cleanName = name?.Trim().ToLowerInvariant();
        var cleanValue = value?.Trim();

        if (!IsHeaderName(cleanName)) return HeaderStatus.InvalidName;
        if (!IsHeaderValue(cleanValue)) return HeaderStatus.InvalidValue;

        if (_auth.ReservedHeaderNames.Contains(cleanName, StringComparer.OrdinalIgnoreCase))
        {
            return HeaderStatus.ReservedName;
        }

        // The unique index would catch this anyway; asking first turns a constraint violation
        // into a message the console can show.
        var existing = await users.FindByHeaderAsync(cleanName!, cleanValue!);
        if (existing is not null && !string.Equals(existing.Email, email, StringComparison.Ordinal))
        {
            return HeaderStatus.Taken;
        }

        await users.SetHeaderAsync(email, cleanName, cleanValue);
        InvalidateHeaderNames();

        return HeaderStatus.Ok;
    }

    public async Task ClearHeaderAsync(string email)
    {
        await users.SetHeaderAsync(email, null, null);
        InvalidateHeaderNames();
    }

    /// <summary>The account presenting this header name and value, or null.</summary>
    public async Task<UserAccount?> ResolveHeaderAsync(string? name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name) || value is null) return null;

        return await users.FindByHeaderAsync(name.Trim().ToLowerInvariant(), value);
    }

    /// <summary>Which header names are worth looking for on an incoming request.</summary>
    public async Task<IReadOnlyList<string>> HeaderNamesAsync()
    {
        if (DateTimeOffset.UtcNow - _headerNamesLoadedAt < HeaderNameCacheLifetime) return _headerNames;

        await _headerNamesGate.WaitAsync();
        try
        {
            if (DateTimeOffset.UtcNow - _headerNamesLoadedAt < HeaderNameCacheLifetime) return _headerNames;

            _headerNames = await users.ListHeaderNamesAsync();
            _headerNamesLoadedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _headerNamesGate.Release();
        }

        return _headerNames;
    }

    /// <summary>
    /// An HTTP field name: RFC 9110's token production, kept to a sane length. Rejecting anything
    /// else is what stops a header name smuggling a newline into a response.
    /// </summary>
    public static bool IsHeaderName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64) return false;

        foreach (var c in name)
        {
            var allowed = c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c is '-' or '_';
            if (!allowed) return false;
        }

        return true;
    }

    /// <summary>Printable ASCII only, for the same reason.</summary>
    public static bool IsHeaderValue(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length is < 8 or > 128) return false;

        foreach (var c in value)
        {
            if (c is < ' ' or > '~') return false;
        }

        return true;
    }

    /// <summary>A value the console can offer as a starting point - long enough that guessing it
    /// is not a strategy.</summary>
    public static string SuggestHeaderValue() =>
        "kv_" + RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 32);

    // ---------- internals ----------

    private OtpDelivery Delivery => sender.CanSend ? OtpDelivery.Email : OtpDelivery.Master;

    private static OtpRequestResult Invalid() => new(
        OtpRequestStatus.InvalidEmail, OtpDelivery.Master,
        Detail: "That does not look like an email address we can use.");

    /// <summary>
    /// Stores the code that will be accepted and, when a relay is configured, sends it. With no
    /// relay the master code is what gets stored, so verification, expiry and the attempt limit
    /// all work identically either way.
    /// </summary>
    private async Task<OtpRequestResult> IssueCodeAsync(string email, CancellationToken cancellationToken)
    {
        await users.PurgeExpiredAsync();

        var emailing = sender.CanSend;
        var code = emailing ? NewCode() : _auth.MasterOtp;
        var expiresAt = DateTimeOffset.UtcNow + _auth.OtpLifetime;

        await users.StoreOtpAsync(email, HashCode(email, code), expiresAt);

        if (!emailing)
        {
            logger.LogWarning(
                "No SMTP relay configured - {Email} must sign in with the master code from Auth:MasterOtp.", email);

            return new OtpRequestResult(
                OtpRequestStatus.Sent,
                OtpDelivery.Master,
                Code: _auth.RevealMasterOtp ? code : null,
                Detail: "No mail relay is configured, so the master code from appsettings will be accepted.");
        }

        if (!await sender.SendAsync(email, code, cancellationToken))
        {
            await users.DeleteOtpAsync(email);

            return new OtpRequestResult(
                OtpRequestStatus.DeliveryFailed, OtpDelivery.Email,
                Detail: "The code could not be sent. Check the address, or try again in a moment.");
        }

        return new OtpRequestResult(
            OtpRequestStatus.Sent, OtpDelivery.Email,
            Detail: $"A code is on its way to {email}. It is valid for {_auth.OtpLifetimeMinutes} minutes.");
    }

    private void InvalidateHeaderNames() => _headerNamesLoadedAt = DateTimeOffset.MinValue;

    /// <summary>Six digits, uniformly distributed, from a cryptographic RNG.</summary>
    private static string NewCode() => RandomNumberGenerator.GetString("0123456789", 6);

    private static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>
    /// Codes and tokens are stored hashed, so a copy of _users.db is not a set of working
    /// credentials. The email salts the code hash, which stops two accounts that happen to hold
    /// the same code - always the case when the master code is in use - sharing a hash.
    /// </summary>
    private static string HashCode(string email, string code) => Sha256($"{email}:{code}");

    private static string HashToken(string token) => Sha256(token);

    private static string Sha256(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    /// <summary>Fixed-time comparison, so how quickly a wrong code is rejected says nothing about
    /// how nearly right it was.</summary>
    private static bool HashMatches(string stored, string candidate) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(candidate));
}
