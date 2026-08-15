namespace Immanuel.KeyValue.Core;

/// <summary>One signed-up account, as it comes back from the accounts database.</summary>
public sealed class UserAccount
{
    public string Email { get; set; } = "";

    /// <summary>The folder holding this account's app-key databases - the email with "@"
    /// replaced by "_at_". Stored rather than re-derived so a request never has to parse it.</summary>
    public string Folder { get; set; } = "";

    /// <summary>The HTTP header this account authenticates its API calls with, lowercased.
    /// Null until the user creates one in the console.</summary>
    public string? HeaderName { get; set; }

    public string? HeaderValue { get; set; }

    public string CreatedAt { get; set; } = "";

    /// <summary>When the address was first proved by entering a code. Null while unverified.</summary>
    public string? VerifiedAt { get; set; }

    public string? LastLoginAt { get; set; }

    public bool HasHeader => !string.IsNullOrEmpty(HeaderName) && HeaderValue is not null;
}

/// <summary>How a request to send a one-time password ended.</summary>
public enum OtpRequestStatus
{
    Sent,
    /// <summary>The address is not one we accept.</summary>
    InvalidEmail,
    /// <summary>Sign-in was asked for on an address that never signed up.</summary>
    UnknownAccount,
    /// <summary>Sign-up was asked for on an address that already has an account.</summary>
    AlreadyRegistered,
    /// <summary>SMTP is configured but the message would not go out.</summary>
    DeliveryFailed,
}

/// <summary>How the code reached the user, which is what the console shows in its hint line.</summary>
public enum OtpDelivery
{
    /// <summary>Emailed through the configured relay.</summary>
    Email,
    /// <summary>No SMTP configured, so the master code from appsettings is what will be accepted.</summary>
    Master,
}

/// <summary>
/// The outcome of asking for a code. <paramref name="Code"/> is only ever populated for
/// <see cref="OtpDelivery.Master"/> with <see cref="AuthOptions.RevealMasterOtp"/> turned on -
/// a real emailed code never comes back over the API.
/// </summary>
public readonly record struct OtpRequestResult(
    OtpRequestStatus Status, OtpDelivery Delivery, string? Code = null, string? Detail = null)
{
    public bool IsOk => Status == OtpRequestStatus.Sent;
}

/// <summary>How a code check ended.</summary>
public enum OtpVerifyStatus
{
    Ok,
    InvalidEmail,
    /// <summary>No code outstanding for this address - never requested, or already used.</summary>
    NoCode,
    Expired,
    /// <summary>Wrong code. The outstanding one survives until the attempt limit is spent.</summary>
    Incorrect,
    /// <summary>Too many wrong guesses; the code was discarded and a new one must be requested.</summary>
    TooManyAttempts,
}

/// <summary>The outcome of checking a code, carrying the session on success.</summary>
public readonly record struct OtpVerifyResult(
    OtpVerifyStatus Status, string? Token = null, DateTimeOffset ExpiresAt = default, UserAccount? Account = null)
{
    public bool IsOk => Status == OtpVerifyStatus.Ok;
}

/// <summary>How setting an account's custom API header ended.</summary>
public enum HeaderStatus
{
    Ok,
    /// <summary>Not a legal HTTP token, or too long.</summary>
    InvalidName,
    /// <summary>Empty, too long, or containing characters that cannot travel in a header.</summary>
    InvalidValue,
    /// <summary>The server or a proxy in front of it already gives this header a meaning.</summary>
    ReservedName,
    /// <summary>Another account already presents this exact name and value.</summary>
    Taken,
}

/// <summary>Who is making an API call, resolved from the custom header or a console session.</summary>
public sealed record Caller(string Email, string Folder, CallerSource Source);

/// <summary>Which credential identified the caller.</summary>
public enum CallerSource
{
    /// <summary>The account's own custom HTTP header.</summary>
    ApiHeader,
    /// <summary>An Authorization: Bearer console session.</summary>
    Session,
}
