using Immanuel.KeyValue.Core;
using Immanuel.KeyValue.Web.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Immanuel.KeyValue.Web.Controllers;

/// <summary>
/// The v2 API: ordinary REST over the same data the v1 endpoints see. It adds the things v1
/// never had - listing your keys, deleting one, stepping a counter by more than one, and
/// sending values in the request body so they are not limited to what fits in a URL segment.
///
/// App keys issued to an account are reachable only by that account, which means sending its
/// custom API header. Anonymous app keys - everything v1 ever issued - stay open to anyone
/// holding the key, exactly as before.
/// </summary>
[ApiController]
[Route("api/v2")]
[Produces("application/json")]
public sealed class StoreController(KeyValueStore store, CallerContext caller) : ControllerBase
{
    /// <summary>
    /// Issues a new app key. With an account's API header on the request the key is issued to
    /// that account and lands in its folder; without one it is anonymous.
    /// </summary>
    [HttpPost("appkeys")]
    [ProducesResponseType<AppKeyCreatedResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateAppKey()
    {
        var appKey = await store.CreateAppKeyAsync(
            ClientInfo.IpAddress(HttpContext), ClientInfo.UserAgent(HttpContext), caller.Account);

        var response = new AppKeyCreatedResponse(
            appKey,
            caller.IsSignedIn
                ? "Issued to your account. It is listed in the console, so it cannot be lost."
                : "Save this key - it is the only way back to your data, and it cannot be recovered.");

        return CreatedAtAction(nameof(GetAppKey), new { appkey = appKey }, response);
    }

    /// <summary>What is known about one app key.</summary>
    [HttpGet("appkeys/{appkey}")]
    [ProducesResponseType<AppKeyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAppKey(string appkey)
    {
        if (!store.AppKeyExists(appkey)) return AppKeyNotFound(appkey);
        if (Denied(appkey) is { } denied) return denied;

        var info = await store.GetAppKeyInfoAsync(appkey);
        if (info is null) return AppKeyNotFound(appkey);

        return Ok(new AppKeyResponse(info.ClientKey, info.KeyCount, info.CreatedAt, info.LastAccessAt));
    }

    /// <summary>Every key stored under one app key.</summary>
    [HttpGet("appkeys/{appkey}/keys")]
    [ProducesResponseType<IEnumerable<ValueResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListKeys(string appkey)
    {
        if (Denied(appkey) is { } denied) return denied;

        var entries = await store.ListAsync(appkey);
        if (entries is null) return AppKeyNotFound(appkey);

        return Ok(entries.Select(ToResponse));
    }

    /// <summary>Reads one key.</summary>
    [HttpGet("appkeys/{appkey}/keys/{key}")]
    [ProducesResponseType<ValueResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetValue(string appkey, string key)
    {
        if (!store.AppKeyExists(appkey)) return AppKeyNotFound(appkey);
        if (Denied(appkey) is { } denied) return denied;

        var entry = await store.GetEntryAsync(appkey, key);
        if (entry is null) return KeyNotFound(appkey, key);

        return Ok(ToResponse(entry));
    }

    /// <summary>Creates or overwrites one key.</summary>
    [HttpPut("appkeys/{appkey}/keys/{key}")]
    [ProducesResponseType<ValueResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValueResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetValue(string appkey, string key, [FromBody] SetValueRequest request)
    {
        if (Denied(appkey) is { } denied) return denied;

        var result = await store.SetValueAsync(
            appkey, key, request?.Value, ClientInfo.IpAddress(HttpContext), ClientInfo.UserAgent(HttpContext));

        if (!result.IsOk) return Failure(result.Status, appkey, key);

        var entry = await store.GetEntryAsync(appkey, key);
        var body = entry is null ? null : ToResponse(entry);

        return result.Created
            ? CreatedAtAction(nameof(GetValue), new { appkey, key }, body)
            : Ok(body);
    }

    /// <summary>Removes one key.</summary>
    [HttpDelete("appkeys/{appkey}/keys/{key}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteValue(string appkey, string key)
    {
        if (Denied(appkey) is { } denied) return denied;

        var status = await store.DeleteAsync(appkey, key);
        return status == StoreStatus.Ok ? NoContent() : Failure(status, appkey, key);
    }

    /// <summary>
    /// Steps a counter. The key is created at zero if it does not exist yet, so a visit counter
    /// needs no setup: just call this on every page load.
    /// </summary>
    [HttpPost("appkeys/{appkey}/keys/{key}/increment")]
    [ProducesResponseType<ValueResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Increment(string appkey, string key, [FromBody] AdjustRequest? request)
    {
        if (Denied(appkey) is { } denied) return denied;

        var result = await store.AdjustAsync(appkey, key, request?.By ?? 1);

        if (result.Status == StoreStatus.NotNumeric)
        {
            return Problem(
                title: "Value is not a number",
                detail: $"'{key}' holds '{result.Value}', which cannot be incremented. Counters must hold a whole number.",
                statusCode: StatusCodes.Status409Conflict);
        }

        if (!result.IsOk) return Failure(result.Status, appkey, key);

        var entry = await store.GetEntryAsync(appkey, key);
        return entry is null ? KeyNotFound(appkey, key) : Ok(ToResponse(entry));
    }

    /// <summary>Totals across the whole service.</summary>
    [HttpGet("stats")]
    [ProducesResponseType<StatsResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats()
    {
        var stats = await store.GetStatsAsync();
        return Ok(new StatsResponse(stats.AppKeys, stats.Keys));
    }

    private IActionResult? Denied(string appKey) => this.Denied(store, caller, appKey);

    private static ValueResponse ToResponse(KeyValueEntry entry) =>
        new(entry.KeyName, entry.KeyVal, entry.CreatedAt, entry.UpdatedAt);

    private IActionResult AppKeyNotFound(string appKey) => Problem(
        title: "Unknown app key",
        detail: $"'{appKey}' has not been issued. POST /api/v2/appkeys to get one.",
        statusCode: StatusCodes.Status404NotFound);

    private IActionResult KeyNotFound(string appKey, string key) => Problem(
        title: "Unknown key",
        detail: $"'{key}' is not stored under app key '{appKey}'.",
        statusCode: StatusCodes.Status404NotFound);

    private IActionResult Failure(StoreStatus status, string appKey, string key) => status switch
    {
        StoreStatus.AppKeyNotFound => AppKeyNotFound(appKey),
        StoreStatus.KeyNotFound => KeyNotFound(appKey, key),

        StoreStatus.KeyLimitReached => Problem(
            title: "Key limit reached",
            detail: $"App key '{appKey}' is holding as many keys as it is allowed. Delete one before adding another.",
            statusCode: StatusCodes.Status409Conflict),

        StoreStatus.NotNumeric => Problem(
            title: "Value is not a number",
            detail: $"'{key}' does not hold a whole number.",
            statusCode: StatusCodes.Status409Conflict),

        _ => Problem(
            title: "Invalid request",
            detail: "Check the app key is 8 characters of a-z/0-9, and that the key and value are within the documented length limits.",
            statusCode: StatusCodes.Status400BadRequest),
    };
}
