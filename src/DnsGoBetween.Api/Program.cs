using DnsGoBetween.Api.Auth;
using DnsGoBetween.Api.Health;
using DnsGoBetween.Core.Configuration;
using DnsGoBetween.Core.Interfaces;
using DnsGoBetween.Infrastructure.Audit;
using DnsGoBetween.Infrastructure.Dns;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();

// ── Options ──────────────────────────────────────────────────────────────────
builder.Services.Configure<DnsOptions>(
    builder.Configuration.GetSection(DnsOptions.SectionName));

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
                if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
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
    // Fallback: require authenticated user for every endpoint
    options.FallbackPolicy = options.DefaultPolicy;

    options.AddPolicy("ReadPolicy",  p => p.RequireAuthenticatedUser());
    options.AddPolicy("WritePolicy", p => p.RequireRole("DnsAdmins", "Domain Admins"));
});

// ── Application services ──────────────────────────────────────────────────────
builder.Services.AddScoped<IPowerShellDnsExecutor, LocalPowerShellDnsExecutor>();
builder.Services.AddScoped<IDnsRecordService, DnsRecordService>();
builder.Services.AddSingleton<IAuditLogger, StructuredAuditLogger>();

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

// ─────────────────────────────────────────────────────────────────────────────
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
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
    Predicate = _ => false
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
}).AllowAnonymous();

app.Run();
