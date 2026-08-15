using Immanuel.KeyValue.Core;
using Microsoft.AspNetCore.Mvc;

namespace Immanuel.KeyValue.Web.Auth;

/// <summary>
/// The one rule that decides whether a request may touch an app key, shared by the v1 and v2
/// controllers so the two can never drift apart on it.
///
/// An app key issued anonymously - which is every key v1 ever handed out - stays open to whoever
/// holds it. A key issued to an account is reachable only by that account, and the check is a
/// file-system lookup rather than a query, so authorising a request costs nothing.
/// </summary>
public static class AppKeyAccess
{
    /// <summary>
    /// Null when the request may proceed, or the problem response to return when it may not.
    /// </summary>
    public static IActionResult? Denied(
        this ControllerBase controller, KeyValueStore store, CallerContext caller, string appKey)
    {
        var owner = store.OwnerOf(appKey);
        if (owner is null || caller.Is(owner)) return null;

        // The owner's address is never named: knowing an app key should not tell you whose it is.
        return controller.Problem(
            title: "App key belongs to an account",
            detail: $"'{appKey}' was issued to a signed-up account, so calls to it must carry that "
                  + "account's API header. Create or check the header in the console.",
            statusCode: StatusCodes.Status403Forbidden);
    }
}
