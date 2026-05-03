using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;
using DnsGoBetween.Core.Configuration;

namespace DnsGoBetween.Api.Auth;

public sealed class DualChallengeHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DualChallenge";
    private readonly AuthOptions _authOptions;

    public DualChallengeHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<AuthOptions> authOptions)
        : base(options, logger, encoder)
    {
        _authOptions = authOptions.Value;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        => Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append("WWW-Authenticate", "Negotiate");

        if (_authOptions.EnableBasicAuthentication &&
            (!_authOptions.RequireHttpsForBasicAuthentication || Request.IsHttps))
        {
            Response.Headers.Append("WWW-Authenticate", "Basic realm=\"DNS Go-Between\", charset=\"UTF-8\"");
        }

        return Task.CompletedTask;
    }
}