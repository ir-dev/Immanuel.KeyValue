using Immanuel.KeyValue.Core;
using Immanuel.KeyValue.Web.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Immanuel.KeyValue.Web.Controllers;

/// <summary>
/// Sign-up and sign-in. Both are the same two steps - ask for a code, then send it back - and
/// both end in a session token the console puts in <c>Authorization: Bearer</c>.
///
/// There are no passwords to store, reset or leak. When no SMTP relay is configured the master
/// code from <c>Auth:MasterOtp</c> is what gets accepted, which is what makes a fresh checkout
/// usable without any mail setup.
/// </summary>
[ApiController]
[Route("api/v2/auth")]
[Produces("application/json")]
public sealed class AuthController(AccountService accounts, KeyValueStore store, CallerContext caller)
    : ControllerBase
{
    /// <summary>Creates an account and sends it a code.</summary>
    [HttpPost("signup")]
    [ProducesResponseType<OtpSentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SignUp([FromBody] EmailRequest request, CancellationToken cancellationToken)
    {
        if (Disabled() is { } off) return off;

        return Sent(request?.Email, await accounts.SignUpAsync(request?.Email, cancellationToken));
    }

    /// <summary>Sends a code to an account that already exists.</summary>
    [HttpPost("signin")]
    [ProducesResponseType<OtpSentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SignIn([FromBody] EmailRequest request, CancellationToken cancellationToken)
    {
        if (Disabled() is { } off) return off;

        return Sent(request?.Email, await accounts.SignInAsync(request?.Email, cancellationToken));
    }

    /// <summary>Exchanges a code for a session.</summary>
    [HttpPost("verify")]
    [ProducesResponseType<SessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Verify([FromBody] VerifyRequest request)
    {
        if (Disabled() is { } off) return off;

        var result = await accounts.VerifyAsync(request?.Email, request?.Code);

        if (!result.IsOk) return VerifyFailure(result.Status);

        var account = result.Account!;

        return Ok(new SessionResponse(
            result.Token!,
            Timestamps.Format(result.ExpiresAt),
            await AccountResponses.BuildAsync(account, store)));
    }

    /// <summary>Ends the session the request was made with.</summary>
    /// <remarks>Named EndSession because ControllerBase already has a SignOut() of its own,
    /// which belongs to cookie authentication and is not what this does.</remarks>
    [HttpPost("signout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> EndSession()
    {
        var header = Request.Headers.Authorization.ToString();

        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await accounts.SignOutAsync(header["Bearer ".Length..]);
        }

        return NoContent();
    }

    /// <summary>
    /// What the sign-in form needs to know before it draws itself: whether accounts are on at
    /// all, whether codes are really emailed, and who the caller already is.
    /// </summary>
    [HttpGet("state")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult State() => Ok(new
    {
        enabled = accounts.Enabled,
        delivery = accounts.CanEmail ? "email" : "master",
        signedInAs = caller.Account?.Email,
    });

    private IActionResult? Disabled() => accounts.Enabled
        ? null
        : Problem(
            title: "Accounts are turned off",
            detail: "This deployment runs with Auth:Enabled set to false. The anonymous API is unaffected.",
            statusCode: StatusCodes.Status404NotFound);

    private IActionResult Sent(string? email, OtpRequestResult result)
    {
        if (result.IsOk)
        {
            return Ok(new OtpSentResponse(
                UserFolder.NormalizeEmail(email) ?? "",
                result.Delivery == OtpDelivery.Email ? "email" : "master",
                result.Detail ?? "A code has been sent.",
                result.Code));
        }

        return result.Status switch
        {
            OtpRequestStatus.AlreadyRegistered => Problem(
                title: "Already registered",
                detail: result.Detail,
                statusCode: StatusCodes.Status409Conflict),

            OtpRequestStatus.UnknownAccount => Problem(
                title: "No such account",
                detail: result.Detail,
                statusCode: StatusCodes.Status404NotFound),

            OtpRequestStatus.DeliveryFailed => Problem(
                title: "Could not send the code",
                detail: result.Detail,
                statusCode: StatusCodes.Status502BadGateway),

            _ => Problem(
                title: "Invalid email address",
                detail: result.Detail,
                statusCode: StatusCodes.Status400BadRequest),
        };
    }

    private IActionResult VerifyFailure(OtpVerifyStatus status) => status switch
    {
        OtpVerifyStatus.NoCode => Problem(
            title: "No code outstanding",
            detail: "Ask for a code first - the last one was used, or never requested.",
            statusCode: StatusCodes.Status401Unauthorized),

        OtpVerifyStatus.Expired => Problem(
            title: "Code expired",
            detail: "That code is past its lifetime. Ask for a new one.",
            statusCode: StatusCodes.Status401Unauthorized),

        OtpVerifyStatus.TooManyAttempts => Problem(
            title: "Too many attempts",
            detail: "That code has been thrown away after too many wrong guesses. Ask for a new one.",
            statusCode: StatusCodes.Status429TooManyRequests),

        OtpVerifyStatus.Incorrect => Problem(
            title: "Wrong code",
            detail: "That code does not match. Check it and try again.",
            statusCode: StatusCodes.Status401Unauthorized),

        _ => Problem(
            title: "Invalid email address",
            detail: "Send the same address the code was requested for.",
            statusCode: StatusCodes.Status400BadRequest),
    };
}
