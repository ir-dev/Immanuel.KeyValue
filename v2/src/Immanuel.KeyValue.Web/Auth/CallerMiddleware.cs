using Immanuel.KeyValue.Core;

namespace Immanuel.KeyValue.Web.Auth;

/// <summary>
/// Works out who is calling, from either or both of two credentials:
///
/// <list type="bullet">
/// <item>The account's own custom header, whatever name and value the user chose in the console.
/// This is the credential meant for application code, and the console pre-fills it into every
/// request it makes.</item>
/// <item><c>Authorization: Bearer &lt;token&gt;</c> - a console session, from signing in with a
/// code. Only this one can manage the account.</item>
/// </list>
///
/// Neither is required. A request carrying neither is anonymous and can still use every app key
/// that has no owner, which is what keeps the v1 API working exactly as it always has.
///
/// Because the header name is chosen by the user, there is nothing fixed to look for. Rather than
/// query once per header the request happens to carry, the middleware asks
/// <see cref="AccountService"/> for the (small, cached) set of names some account has actually
/// claimed, and only looks those up.
/// </summary>
public sealed class CallerMiddleware(RequestDelegate next)
{
    private const string BearerPrefix = "Bearer ";

    public async Task InvokeAsync(HttpContext context, CallerContext caller, AccountService accounts)
    {
        if (accounts.Enabled)
        {
            caller.HeaderAccount = await ResolveHeaderAsync(context, accounts);
            caller.SessionAccount = await ResolveSessionAsync(context, accounts);
        }

        await next(context);
    }

    private static async Task<UserAccount?> ResolveHeaderAsync(HttpContext context, AccountService accounts)
    {
        foreach (var name in await accounts.HeaderNamesAsync())
        {
            if (!context.Request.Headers.TryGetValue(name, out var values)) continue;

            var value = values.ToString();
            if (string.IsNullOrEmpty(value)) continue;

            if (await accounts.ResolveHeaderAsync(name, value) is { } account) return account;
        }

        return null;
    }

    private static async Task<UserAccount?> ResolveSessionAsync(HttpContext context, AccountService accounts)
    {
        var header = context.Request.Headers.Authorization.ToString();

        return header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? await accounts.ResolveSessionAsync(header[BearerPrefix.Length..].Trim())
            : null;
    }
}
