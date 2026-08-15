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
