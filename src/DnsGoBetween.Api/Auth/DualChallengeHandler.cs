using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;

namespace DnsGoBetween.Api.Auth;

public sealed class DualChallengeHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DualChallenge";

    public DualChallengeHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        => Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append("WWW-Authenticate", "Negotiate");
        Response.Headers.Append("WWW-Authenticate", "Basic realm=\"DNS Go-Between\", charset=\"UTF-8\"");
        return Task.CompletedTask;
    }
}