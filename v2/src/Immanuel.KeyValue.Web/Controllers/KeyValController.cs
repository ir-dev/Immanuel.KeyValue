using Immanuel.KeyValue.Core;
using Immanuel.KeyValue.Web.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Immanuel.KeyValue.Web.Controllers;

/// <summary>
/// The original v1 API, kept URL-for-URL so code written against keyvalue.immanuel.co over the
/// last decade keeps working untouched.
///
/// Two details are deliberate. Every action returns <see cref="JsonResult"/> rather than a bare
/// string, because ASP.NET Web API 2 serialised string returns as JSON (quotes and all) and
/// ASP.NET Core would otherwise send text/plain - a change callers that JSON.parse the body
/// would notice. And the odd casing of these routes is v1's; routing is case-insensitive, so
/// existing callers match either way.
///
/// New code should prefer <see cref="StoreController"/> under /api/v2.
///
/// Accounts changed nothing here. A key issued anonymously stays open to whoever holds it, which
/// is every key this API ever handed out; only keys issued to a signed-up account need that
/// account's header, and no v1 caller has one of those.
/// </summary>
[ApiController]
[Route("api/KeyVal")]
[Produces("application/json")]
public sealed class KeyValController(KeyValueStore store, CallerContext caller) : ControllerBase
{
    /// <summary>Issues a new 8-character app key.</summary>
    [HttpGet("GetAppKey")]
    public async Task<IActionResult> GetAppKey()
    {
        var appKey = await store.CreateAppKeyAsync(
            ClientInfo.IpAddress(HttpContext), ClientInfo.UserAgent(HttpContext), caller.Account);

        return new JsonResult(appKey);
    }

    /// <summary>Total number of key-value pairs stored across the whole service.</summary>
    [HttpGet("GetCount")]
    public async Task<IActionResult> GetCount()
    {
        var stats = await store.GetStatsAsync();
        return new JsonResult(stats.Keys);
    }

    /// <summary>Reads a value. Returns an empty string when it does not exist, exactly like v1.</summary>
    [HttpGet("GetValue/{appkey}/{key}")]
    public async Task<IActionResult> GetValue(string appkey, string key)
    {
        if (Denied(appkey) is { } denied) return denied;

        var value = await store.GetValueAsync(appkey, key);
        return new JsonResult(value ?? "");
    }

    /// <summary>Creates or overwrites a value.</summary>
    /// <remarks>
    /// v1 always answered true, even when the write did nothing. This still answers true on
    /// success, but a rejected write now comes back as 400/404/409 instead of a cheerful lie.
    /// </remarks>
    [HttpPost("UpdateValue/{appkey}/{key}")]
    [HttpPost("UpdateValue/{appkey}/{key}/{value}")]
    public async Task<IActionResult> UpdateValue(string appkey, string key, string? value = null)
    {
        if (Denied(appkey) is { } denied) return denied;

        var result = await store.SetValueAsync(
            appkey, key, value, ClientInfo.IpAddress(HttpContext), ClientInfo.UserAgent(HttpContext));

        if (!result.IsOk) return Failure(result.Status, appkey, key);

        return new JsonResult(true);
    }

    /// <summary>
    /// Applies an action to a value. v1 understood only "increment"; "decrement" now works too,
    /// which is what the front page always claimed. Unknown actions are rejected.
    /// </summary>
    [HttpPost("ActOnValue/{appkey}/{key}/{value}")]
    public async Task<IActionResult> ActOnValue(string appkey, string key, string value)
    {
        if (Denied(appkey) is { } denied) return denied;

        var by = value?.Trim().ToLowerInvariant() switch
        {
            "increment" => 1L,
            "decrement" => -1L,
            _ => 0L,
        };

        if (by == 0)
        {
            return Problem(
                title: "Unknown action",
                detail: $"'{value}' is not a supported action. Use 'increment' or 'decrement'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await store.AdjustAsync(appkey, key, by);

        // These two strings are v1's, typo included, because callers may be matching on them.
        return result.Status switch
        {
            StoreStatus.Ok => new JsonResult("Increment Successful"),
            StoreStatus.NotNumeric => new JsonResult("Increment Failed, increment applied on string charecters"),
            _ => Failure(result.Status, appkey, key),
        };
    }

    /// <summary>Echoes the caller's IP address, as seen by the server.</summary>
    [HttpGet("GetIp")]
    public IActionResult GetIp() => new JsonResult(ClientInfo.IpAddress(HttpContext) ?? "");

    private IActionResult? Denied(string appKey) => this.Denied(store, caller, appKey);

    private IActionResult Failure(StoreStatus status, string appKey, string key) => status switch
    {
        StoreStatus.AppKeyNotFound => Problem(
            title: "Unknown app key",
            detail: $"'{appKey}' has not been issued. Call GET /api/KeyVal/GetAppKey to get one.",
            statusCode: StatusCodes.Status404NotFound),

        StoreStatus.KeyNotFound => Problem(
            title: "Unknown key",
            detail: $"'{key}' is not stored under app key '{appKey}'.",
            statusCode: StatusCodes.Status404NotFound),

        StoreStatus.KeyLimitReached => Problem(
            title: "Key limit reached",
            detail: $"App key '{appKey}' is holding as many keys as it is allowed.",
            statusCode: StatusCodes.Status409Conflict),

        _ => Problem(
            title: "Invalid request",
            detail: "Check the app key is 8 characters of a-z/0-9, and that the key and value are within the documented length limits.",
            statusCode: StatusCodes.Status400BadRequest),
    };
}
