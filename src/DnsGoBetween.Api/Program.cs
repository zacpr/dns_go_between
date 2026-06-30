using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DnsGoBetween.Api.Auth;
using DnsGoBetween.Api.Health;
using DnsGoBetween.Api.Security;
using DnsGoBetween.Core.Configuration;
using DnsGoBetween.Core.Interfaces;
using DnsGoBetween.Infrastructure.Audit;
using DnsGoBetween.Infrastructure.Dns;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting.WindowsServices;

[assembly: SupportedOSPlatform("windows")]

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();

// ── Options ──────────────────────────────────────────────────────────────────
builder.Services.Configure<DnsOptions>(
    builder.Configuration.GetSection(DnsOptions.SectionName));
builder.Services.Configure<AuthOptions>(
    builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<TlsOptions>(
    builder.Configuration.GetSection(TlsOptions.SectionName));

var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
var tlsOptions = builder.Configuration.GetSection(TlsOptions.SectionName).Get<TlsOptions>() ?? new TlsOptions();
var certificate = ResolveServerCertificate(tlsOptions);
var primaryUsesHttps = certificate is not null;
var secondaryHttpEnabled = tlsOptions.EnableHttp &&
                           tlsOptions.HttpPort > 0 &&
                           tlsOptions.HttpPort != tlsOptions.HttpsPort;

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(tlsOptions.HttpsPort, listen =>
    {
        if (!primaryUsesHttps)
        {
            // If no certificate is available, keep the service reachable on HTTP.
            // This avoids protocol mismatch errors on hosts without TLS material.
            return;
        }
        else
        {
            listen.UseHttps(certificate!);
        }
    });

    if (secondaryHttpEnabled)
    {
        kestrel.ListenAnyIP(tlsOptions.HttpPort);
    }
});

// ── Authentication — Windows Negotiate + Basic (for proxied/non-Windows clients) ──
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "Dynamic";
        options.DefaultAuthenticateScheme = "Dynamic";
        options.DefaultChallengeScheme = "Dynamic";
    })
    .AddPolicyScheme("Dynamic", "Negotiate or Basic", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrWhiteSpace(authorization))
            {
                if (authOptions.EnableBasicAuthentication &&
                    authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                    return BasicAuthenticationHandler.SchemeName;

                if (authorization.StartsWith("Negotiate ", StringComparison.OrdinalIgnoreCase))
                    return NegotiateDefaults.AuthenticationScheme;
            }

            return NegotiateDefaults.AuthenticationScheme;
        };
        options.ForwardChallenge = DualChallengeHandler.SchemeName;
    })
    .AddNegotiate()
    .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(
        BasicAuthenticationHandler.SchemeName, _ => { })
    .AddScheme<AuthenticationSchemeOptions, DualChallengeHandler>(
        DualChallengeHandler.SchemeName, _ => { });

// ── Authorization ────────────────────────────────────────────────────────────
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = options.DefaultPolicy;

    options.AddPolicy("ReadPolicy", p => p.RequireAuthenticatedUser());
    options.AddPolicy("WritePolicy", p => p.RequireRole("DnsAdmins", "Domain Admins"));
});

// ── Application services ──────────────────────────────────────────────────────
builder.Services.AddScoped<IPowerShellDnsExecutor, LocalPowerShellDnsExecutor>();
builder.Services.AddScoped<IDnsRecordService, DnsRecordService>();
builder.Services.AddSingleton<IAuditLogger, StructuredAuditLogger>();
builder.Services.AddSingleton<FileIpAccessPolicy>();
builder.Services.AddSingleton<BasicAuthenticationAttemptLimiter>();
builder.Services.AddSingleton<DnsReadinessState>();
builder.Services.AddHostedService<DnsReadinessProbeService>();

// ── Web / Blazor ──────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();

// ── Swagger ───────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "DNS Go-Between API", Version = "v1" });
});

// ── Health checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck<DnsCommandHealthCheck>("dns-cmdlet-readiness", tags: ["ready"]);

var app = builder.Build();

app.Logger.LogInformation(
    "Startup listeners: PrimaryPort={PrimaryPort} PrimaryProtocol={PrimaryProtocol} SecondaryHttpEnabled={SecondaryHttpEnabled} SecondaryHttpPort={SecondaryHttpPort} RedirectHttpToHttps={RedirectHttpToHttps} AutoSelectMachineCertificate={AutoSelectMachineCertificate} CertificateSource={CertificateSource}",
    tlsOptions.HttpsPort,
    primaryUsesHttps ? "HTTPS" : "HTTP",
    secondaryHttpEnabled,
    secondaryHttpEnabled ? tlsOptions.HttpPort : null,
    tlsOptions.RedirectHttpToHttps,
    tlsOptions.AutoSelectMachineCertificate,
    DescribeCertificateSource(tlsOptions, certificate));

app.Logger.LogInformation(
    "Health endpoint access: /health/* allows anonymous remote callers.");

// Swagger UI is exposed in all environments so operators can use the API Docs
// link from the Blazor UI on production installs. The endpoints themselves
// still require authentication via the normal authn/authz pipeline.
app.UseSwagger();
app.UseSwaggerUI();

if (tlsOptions.RedirectHttpToHttps)
{
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    var policy = context.RequestServices.GetRequiredService<FileIpAccessPolicy>();
    if (!policy.IsAllowed(context.Connection.RemoteIpAddress, out var reason))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Access denied",
            Detail = reason
        });
        return;
    }

    await next();
});

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapControllers();
app.MapRazorComponents<DnsGoBetween.Api.Components.App>()
    .AddInteractiveServerRenderMode();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = WriteMinimalHealthResponseAsync
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = WriteMinimalHealthResponseAsync
}).AllowAnonymous();

app.Run();

static X509Certificate2? ResolveServerCertificate(TlsOptions tls)
{
    if (!string.IsNullOrWhiteSpace(tls.Certificate.PfxPath))
    {
        var pfxPath = tls.Certificate.PfxPath.Trim();
        if (!File.Exists(pfxPath))
        {
            throw new InvalidOperationException($"TLS PFX file not found: {pfxPath}");
        }

        return new X509Certificate2(
            pfxPath,
            tls.Certificate.PfxPassword,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
    }

    if (TryResolveFromStore(tls.Certificate, out var configuredCert))
    {
        return configuredCert;
    }

    if (!tls.AutoSelectMachineCertificate)
    {
        return null;
    }

    if (TryResolveMachineCertificate(out var machineCert))
    {
        return machineCert;
    }

    return null;
}

static bool TryResolveFromStore(TlsCertificateOptions certOptions, out X509Certificate2? cert)
{
    cert = null;

    if (!Enum.TryParse<StoreName>(certOptions.StoreName, true, out var storeName))
    {
        storeName = StoreName.My;
    }

    if (!Enum.TryParse<StoreLocation>(certOptions.StoreLocation, true, out var storeLocation))
    {
        storeLocation = StoreLocation.LocalMachine;
    }

    using var store = new X509Store(storeName, storeLocation);
    store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

    var candidates = store.Certificates
        .OfType<X509Certificate2>()
        .Where(c => c.HasPrivateKey && c.NotAfter > DateTime.UtcNow)
        .Where(HasServerAuthenticationEku)
        .ToList();

    if (!string.IsNullOrWhiteSpace(certOptions.Thumbprint))
    {
        var wanted = certOptions.Thumbprint.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase);
        cert = candidates.FirstOrDefault(c =>
            string.Equals(c.Thumbprint?.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase),
                wanted,
                StringComparison.OrdinalIgnoreCase));
        return cert is not null;
    }

    if (!string.IsNullOrWhiteSpace(certOptions.Subject))
    {
        var wanted = certOptions.Subject.Trim();
        cert = candidates
            .Where(c => SubjectMatches(c, wanted))
            .OrderByDescending(c => c.NotAfter)
            .FirstOrDefault();
        return cert is not null;
    }

    return false;
}

static bool TryResolveMachineCertificate(out X509Certificate2? cert)
{
    cert = null;
    var hostNames = GetCandidateHostNames();

    using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
    store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

    cert = store.Certificates
        .OfType<X509Certificate2>()
        .Where(c => c.HasPrivateKey && c.NotAfter > DateTime.UtcNow)
        .Where(HasServerAuthenticationEku)
        .Where(c => hostNames.Any(h => SubjectMatches(c, h)))
        .OrderByDescending(c => c.NotAfter)
        .FirstOrDefault();

    return cert is not null;
}

static bool SubjectMatches(X509Certificate2 cert, string expected)
{
    if (string.IsNullOrWhiteSpace(expected))
    {
        return false;
    }

    var dnsName = cert.GetNameInfo(X509NameType.DnsName, false);
    var simpleName = cert.GetNameInfo(X509NameType.SimpleName, false);

    return string.Equals(dnsName, expected, StringComparison.OrdinalIgnoreCase)
        || string.Equals(simpleName, expected, StringComparison.OrdinalIgnoreCase)
        || cert.Subject.Contains($"CN={expected}", StringComparison.OrdinalIgnoreCase)
        || cert.Subject.Contains(expected, StringComparison.OrdinalIgnoreCase);
}

static Task WriteMinimalHealthResponseAsync(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";
    return context.Response.WriteAsJsonAsync(new
    {
        status = report.Status.ToString()
    });
}

static bool HasServerAuthenticationEku(X509Certificate2 cert)
{
    const string serverAuthOid = "1.3.6.1.5.5.7.3.1";

    foreach (var extension in cert.Extensions.OfType<X509Extension>())
    {
        if (extension is not X509EnhancedKeyUsageExtension eku)
        {
            continue;
        }

        return eku.EnhancedKeyUsages
            .OfType<Oid>()
            .Any(oid => string.Equals(oid.Value, serverAuthOid, StringComparison.Ordinal));
    }

    return true;
}

static string DescribeCertificateSource(TlsOptions tlsOptions, X509Certificate2? certificate)
{
    if (certificate is null)
    {
        return "None";
    }

    if (!string.IsNullOrWhiteSpace(tlsOptions.Certificate.PfxPath))
    {
        return "PfxPath";
    }

    if (!string.IsNullOrWhiteSpace(tlsOptions.Certificate.Thumbprint) ||
        !string.IsNullOrWhiteSpace(tlsOptions.Certificate.Subject))
    {
        return "ConfiguredStoreLookup";
    }

    return "MachineStoreAuto";
}

static IEnumerable<string> GetCandidateHostNames()
{
    var list = new List<string>();

    var machine = Environment.MachineName;
    if (!string.IsNullOrWhiteSpace(machine))
    {
        list.Add(machine);
    }

    var domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
    if (!string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(machine))
    {
        list.Add($"{machine}.{domain}");
    }

    try
    {
        var host = Dns.GetHostEntry(Dns.GetHostName());
        if (!string.IsNullOrWhiteSpace(host.HostName))
        {
            list.Add(host.HostName);
        }
    }
    catch
    {
        // Best-effort lookup only.
    }

    return list.Distinct(StringComparer.OrdinalIgnoreCase);
}
