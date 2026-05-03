using System.Net.Http.Headers;
using System.Security.Claims;
using System.Runtime.Versioning;
using System.Text.Encodings.Web;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.DirectoryServices.AccountManagement;
using DnsGoBetween.Api.Security;
using DnsGoBetween.Core.Configuration;

namespace DnsGoBetween.Api.Auth;

[SupportedOSPlatform("windows")]
public sealed class BasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Basic";
    private readonly AuthOptions _authOptions;
    private readonly BasicAuthenticationAttemptLimiter _attemptLimiter;

    public BasicAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<AuthOptions> authOptions,
        BasicAuthenticationAttemptLimiter attemptLimiter)
        : base(options, logger, encoder)
    {
        _authOptions = authOptions.Value;
        _attemptLimiter = attemptLimiter;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorization) ||
            !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        try
        {
            if (!_authOptions.EnableBasicAuthentication)
                return Task.FromResult(AuthenticateResult.Fail("Basic authentication is disabled."));

            if (_authOptions.RequireHttpsForBasicAuthentication && !Request.IsHttps)
                return Task.FromResult(AuthenticateResult.Fail("Basic authentication requires HTTPS."));

            var remoteAddress = Request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (_attemptLimiter.IsLockedOut(remoteAddress, out var retryAfter))
            {
                Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                return Task.FromResult(AuthenticateResult.Fail("Too many failed authentication attempts."));
            }

            var parameter = AuthenticationHeaderValue.Parse(authorization).Parameter;
            if (string.IsNullOrWhiteSpace(parameter))
            {
                _attemptLimiter.RegisterFailure(remoteAddress);
                return Task.FromResult(AuthenticateResult.Fail("Missing Basic credentials."));
            }

            var credentialBytes = Convert.FromBase64String(parameter);
            var credentials = Encoding.UTF8.GetString(credentialBytes);
            var separator = credentials.IndexOf(':');
            if (separator <= 0)
            {
                _attemptLimiter.RegisterFailure(remoteAddress);
                return Task.FromResult(AuthenticateResult.Fail("Invalid Basic credentials format."));
            }

            var userName = credentials[..separator];
            var password = credentials[(separator + 1)..];

            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
            {
                _attemptLimiter.RegisterFailure(remoteAddress);
                return Task.FromResult(AuthenticateResult.Fail("Username and password are required."));
            }

            var principalData = ValidateCredentials(userName, password);
            if (principalData is null)
            {
                _attemptLimiter.RegisterFailure(remoteAddress);
                return Task.FromResult(AuthenticateResult.Fail("Invalid username or password."));
            }

            _attemptLimiter.RegisterSuccess(remoteAddress);

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, principalData.NameIdentifier),
                new(ClaimTypes.Name, principalData.DisplayName)
            };

            foreach (var role in principalData.Roles)
                claims.Add(new Claim(ClaimTypes.Role, role));

            var identity = new ClaimsIdentity(claims, SchemeName);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
        catch (FormatException)
        {
            var remoteAddress = Request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            _attemptLimiter.RegisterFailure(remoteAddress);
            return Task.FromResult(AuthenticateResult.Fail("Invalid Basic credentials encoding."));
        }
        catch (Exception ex)
        {
            var remoteAddress = Request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            _attemptLimiter.RegisterFailure(remoteAddress);
            Logger.LogWarning(ex, "Basic authentication failed.");
            return Task.FromResult(AuthenticateResult.Fail("Authentication failed."));
        }
    }

    private static AuthenticatedPrincipal? ValidateCredentials(string userName, string password)
    {
        foreach (var candidate in BuildCandidates(userName))
        {
            try
            {
                using var context = candidate.CreateContext();
                if (!context.ValidateCredentials(candidate.ValidateUserName, password, ContextOptions.Negotiate))
                    continue;

                using var user = UserPrincipal.FindByIdentity(context, candidate.IdentityType, candidate.IdentityValue);
                var displayName = user?.UserPrincipalName
                    ?? user?.SamAccountName
                    ?? userName;
                var nameIdentifier = user?.Sid?.Value
                    ?? displayName;

                var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (user is not null)
                {
                    try
                    {
                        foreach (var group in user.GetAuthorizationGroups().OfType<GroupPrincipal>())
                        {
                            if (!string.IsNullOrWhiteSpace(group.SamAccountName))
                                roles.Add(group.SamAccountName);
                            if (!string.IsNullOrWhiteSpace(group.Name))
                                roles.Add(group.Name);
                        }
                    }
                    catch
                    {
                    }
                }

                return new AuthenticatedPrincipal(nameIdentifier, displayName, roles.ToArray());
            }
            catch
            {
            }
        }

        return null;
    }

    private static IEnumerable<CredentialCandidate> BuildCandidates(string userName)
    {
        if (userName.Contains('\\'))
        {
            var parts = userName.Split('\\', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                yield return new CredentialCandidate(ContextType.Domain, parts[0], parts[1], IdentityType.SamAccountName, parts[1]);
                yield break;
            }
        }

        if (userName.Contains('@'))
        {
            var parts = userName.Split('@', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                yield return new CredentialCandidate(ContextType.Domain, parts[1], userName, IdentityType.UserPrincipalName, userName);
                yield break;
            }
        }

        yield return new CredentialCandidate(ContextType.Domain, null, userName, IdentityType.SamAccountName, userName);
        yield return new CredentialCandidate(ContextType.Machine, Environment.MachineName, userName, IdentityType.SamAccountName, userName);
    }

    private sealed record CredentialCandidate(
        ContextType ContextType,
        string? ContextName,
        string ValidateUserName,
        IdentityType IdentityType,
        string IdentityValue)
    {
        public PrincipalContext CreateContext()
            => ContextName is null
                ? new PrincipalContext(ContextType)
                : new PrincipalContext(ContextType, ContextName);
    }

    private sealed record AuthenticatedPrincipal(
        string NameIdentifier,
        string DisplayName,
        string[] Roles);
}