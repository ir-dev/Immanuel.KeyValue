using Immanuel.KeyValue.Core;
using Immanuel.KeyValue.Web.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Immanuel.KeyValue.Web.Controllers;

/// <summary>
/// Everything a signed-in account manages about itself: the app keys stored in its folder, and
/// the custom HTTP header its API calls are authenticated with.
///
/// These routes need a console session (<c>Authorization: Bearer</c>) rather than the custom
/// header, so a leaked API header cannot be used to mint more keys or rewrite the credential
/// that leaked.
/// </summary>
[ApiController]
[Route("api/v2/me")]
[Produces("application/json")]
public sealed class AccountController(
    AccountService accounts,
    KeyValueStore store,
    CallerContext caller,
    IOptions<AuthOptions> authOptions) : ControllerBase
{
    private readonly AuthOptions _auth = authOptions.Value;

    /// <summary>The signed-in account, its app keys and its API header.</summary>
    [HttpGet]
    [ProducesResponseType<AccountResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Get()
    {
        if (Account() is not { } account) return NotSignedIn();

        return Ok(await AccountResponses.BuildAsync(account, store));
    }

    /// <summary>Issues an app key into this account's folder.</summary>
    [HttpPost("appkeys")]
    [ProducesResponseType<AppKeyResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateAppKey()
    {
        if (Account() is not { } account) return NotSignedIn();

        var limit = _auth.MaxAppKeysPerUser;
        if (await store.CountAppKeysAsync(account.Email) >= limit)
        {
            return Problem(
                title: "App key limit reached",
                detail: $"This account already holds {limit} app keys. Delete one before issuing another.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var appKey = await store.CreateAppKeyAsync(
            ClientInfo.IpAddress(HttpContext), ClientInfo.UserAgent(HttpContext), account);

        var info = await store.GetAppKeyInfoAsync(appKey);

        return Created(
            $"/api/v2/appkeys/{appKey}",
            new AppKeyResponse(appKey, info?.KeyCount ?? 0, info?.CreatedAt ?? Timestamps.UtcNow(), null));
    }

    /// <summary>Every app key in this account's folder.</summary>
    [HttpGet("appkeys")]
    [ProducesResponseType<IEnumerable<AppKeyResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListAppKeys()
    {
        if (Account() is not { } account) return NotSignedIn();

        return Ok(await AccountResponses.AppKeysAsync(account, store));
    }

    /// <summary>Deletes an app key and everything stored under it.</summary>
    [HttpDelete("appkeys/{appkey}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAppKey(string appkey)
    {
        if (Account() is not { } account) return NotSignedIn();

        // Ownership is decided by which folder the file sits in, so a key belonging to somebody
        // else is indistinguishable from one that does not exist.
        if (!string.Equals(store.OwnerOf(appkey), account.Email, StringComparison.OrdinalIgnoreCase))
        {
            return Problem(
                title: "Unknown app key",
                detail: $"'{appkey}' is not one of this account's app keys.",
                statusCode: StatusCodes.Status404NotFound);
        }

        await store.DeleteAppKeyAsync(appkey);
        return NoContent();
    }

    /// <summary>The custom header this account's API calls are authenticated with.</summary>
    [HttpGet("header")]
    [ProducesResponseType<ApiHeaderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetHeader()
    {
        if (Account() is not { } account) return NotSignedIn();

        if (!account.HasHeader)
        {
            return Problem(
                title: "No API header yet",
                detail: "PUT /api/v2/me/header with a name and value to create one.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Ok(new ApiHeaderResponse(account.HeaderName!, account.HeaderValue!));
    }

    /// <summary>
    /// Creates or replaces the custom header. Both halves are the account's own choice: pick a
    /// name your client can send and a value nobody can guess.
    /// </summary>
    [HttpPut("header")]
    [ProducesResponseType<ApiHeaderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetHeader([FromBody] SetApiHeaderRequest request)
    {
        if (Account() is not { } account) return NotSignedIn();

        var status = await accounts.SetHeaderAsync(account.Email, request?.Name, request?.Value);

        if (status != HeaderStatus.Ok) return HeaderFailure(status);

        return Ok(new ApiHeaderResponse(
            request!.Name!.Trim().ToLowerInvariant(), request.Value!.Trim()));
    }

    /// <summary>Removes the header, after which this account's app keys can only be reached with
    /// a console session.</summary>
    [HttpDelete("header")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ClearHeader()
    {
        if (Account() is not { } account) return NotSignedIn();

        await accounts.ClearHeaderAsync(account.Email);
        return NoContent();
    }

    /// <summary>A name and value the console can pre-fill, so nobody has to invent a secret.</summary>
    [HttpGet("header/suggest")]
    [ProducesResponseType<SuggestedHeaderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult SuggestHeader()
    {
        if (Account() is null) return NotSignedIn();

        return Ok(new SuggestedHeaderResponse("x-keyvalue-token", AccountService.SuggestHeaderValue()));
    }

    /// <summary>
    /// The account behind a console session. Deliberately not <c>caller.Account</c>: the custom
    /// header is a credential for data, and a leaked one must not be able to mint more app keys
    /// or rewrite the credential that leaked.
    /// </summary>
    private UserAccount? Account() => caller.SessionAccount;

    private IActionResult NotSignedIn() => Problem(
        title: "Not signed in",
        detail: "Send Authorization: Bearer <token> from POST /api/v2/auth/verify. The custom API "
              + "header authenticates data calls, not account management.",
        statusCode: StatusCodes.Status401Unauthorized);

    private IActionResult HeaderFailure(HeaderStatus status) => status switch
    {
        HeaderStatus.InvalidName => Problem(
            title: "Invalid header name",
            detail: "Up to 64 characters of a-z, 0-9, '-' and '_'. Header names are case-insensitive "
                  + "and are stored lowercase.",
            statusCode: StatusCodes.Status400BadRequest),

        HeaderStatus.InvalidValue => Problem(
            title: "Invalid header value",
            detail: "Between 8 and 128 printable ASCII characters.",
            statusCode: StatusCodes.Status400BadRequest),

        HeaderStatus.ReservedName => Problem(
            title: "Reserved header name",
            detail: "That header already means something to the server or a proxy in front of it. "
                  + "Pick a name of your own, such as x-yourapp-token.",
            statusCode: StatusCodes.Status400BadRequest),

        _ => Problem(
            title: "Header already in use",
            detail: "Another account presents that exact name and value. Change the value.",
            statusCode: StatusCodes.Status409Conflict),
    };
}

/// <summary>Shared shaping of an account for the API, used by both the auth and account routes.</summary>
internal static class AccountResponses
{
    public static async Task<AccountResponse> BuildAsync(UserAccount account, KeyValueStore store) => new(
        account.Email,
        account.Folder,
        account.CreatedAt,
        account.LastLoginAt,
        account.HasHeader ? new ApiHeaderResponse(account.HeaderName!, account.HeaderValue!) : null,
        await AppKeysAsync(account, store));

    public static async Task<IReadOnlyList<AppKeyResponse>> AppKeysAsync(UserAccount account, KeyValueStore store)
    {
        var keys = await store.ListAppKeysAsync(account.Email);

        return keys
            .Select(info => new AppKeyResponse(info.ClientKey, info.KeyCount, info.CreatedAt, info.LastAccessAt))
            .ToList();
    }
}
