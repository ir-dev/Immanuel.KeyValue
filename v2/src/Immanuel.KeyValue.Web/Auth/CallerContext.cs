using Immanuel.KeyValue.Core;

namespace Immanuel.KeyValue.Web.Auth;

/// <summary>
/// Who is making the current request, if anyone. Populated once per request by
/// <see cref="CallerMiddleware"/> and read by the controllers; a request carrying neither
/// credential leaves everything null, which is the normal case for the anonymous v1 API.
///
/// The two credentials are tracked separately rather than collapsed into one, because they are
/// not interchangeable: the custom API header reaches data, and only a console session may
/// manage the account - including rewriting the header itself. The console sends both at once,
/// so "which one won" is not a question either controller should have to ask.
/// </summary>
public sealed class CallerContext
{
    /// <summary>The account behind the request's custom API header, if it carried a known one.</summary>
    public UserAccount? HeaderAccount { get; set; }

    /// <summary>The account behind the request's Authorization: Bearer session, if valid.</summary>
    public UserAccount? SessionAccount { get; set; }

    /// <summary>
    /// The account this request acts as, for the purpose of reaching data. The API header wins
    /// when both are present, so a console request exercises the credential the user configured.
    /// </summary>
    public UserAccount? Account => HeaderAccount ?? SessionAccount;

    public CallerSource? Source =>
        HeaderAccount is not null ? CallerSource.ApiHeader :
        SessionAccount is not null ? CallerSource.Session : null;

    public bool IsSignedIn => Account is not null;

    /// <summary>True when this request is being made by <paramref name="email"/>.</summary>
    public bool Is(string? email) =>
        email is not null && Account is not null &&
        string.Equals(Account.Email, email, StringComparison.OrdinalIgnoreCase);
}
