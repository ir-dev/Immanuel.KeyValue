namespace Immanuel.KeyValue.Web.Controllers;

/// <summary>A newly issued app key.</summary>
public sealed record AppKeyCreatedResponse(string AppKey, string Message);

/// <summary>What is known about an app key.</summary>
public sealed record AppKeyResponse(string AppKey, long KeyCount, string CreatedAt, string? LastAccessAt);

/// <summary>One stored key.</summary>
public sealed record ValueResponse(string Key, string? Value, string CreatedAt, string UpdatedAt);

/// <summary>
/// Body of a PUT. Sending the value in the body rather than the URL is the reason to prefer the
/// v2 API: values can contain slashes, newlines and anything else a URL path segment cannot hold.
/// </summary>
public sealed record SetValueRequest(string? Value);

/// <summary>Body of an increment. Negative values decrement; omit it to step by one.</summary>
public sealed record AdjustRequest(long By = 1);

/// <summary>Service-wide totals.</summary>
public sealed record StatsResponse(long AppKeys, long Keys);

// ---------- accounts ----------

/// <summary>Body of sign-up and sign-in: the address a code should go to.</summary>
public sealed record EmailRequest(string? Email);

/// <summary>Body of the code check.</summary>
public sealed record VerifyRequest(string? Email, string? Code);

/// <summary>
/// The answer to "send me a code". <paramref name="Delivery"/> is <c>email</c> or <c>master</c>;
/// <paramref name="Code"/> is only ever filled in for <c>master</c> with
/// <c>Auth:RevealMasterOtp</c> turned on, so a real emailed code never comes back over the API.
/// </summary>
public sealed record OtpSentResponse(string Email, string Delivery, string Message, string? Code);

/// <summary>A signed-in session. The token goes in <c>Authorization: Bearer</c>.</summary>
public sealed record SessionResponse(string Token, string ExpiresAt, AccountResponse Account);

/// <summary>
/// One account, as the console sees it. <paramref name="Folder"/> is the directory its app-key
/// databases live in - the email with "@" replaced by "_at_".
/// </summary>
public sealed record AccountResponse(
    string Email,
    string Folder,
    string CreatedAt,
    string? LastLoginAt,
    ApiHeaderResponse? ApiHeader,
    IReadOnlyList<AppKeyResponse> AppKeys);

/// <summary>The custom HTTP header an account authenticates its API calls with.</summary>
public sealed record ApiHeaderResponse(string Name, string Value);

/// <summary>Body of the header create/replace call.</summary>
public sealed record SetApiHeaderRequest(string? Name, string? Value);

/// <summary>A header name and value the console can offer as a starting point.</summary>
public sealed record SuggestedHeaderResponse(string Name, string Value);
