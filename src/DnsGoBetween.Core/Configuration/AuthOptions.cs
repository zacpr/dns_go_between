namespace DnsGoBetween.Core.Configuration;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public bool EnableBasicAuthentication { get; set; } = true;
    public bool RequireHttpsForBasicAuthentication { get; set; } = true;
    public int BasicAuthenticationFailureLimit { get; set; } = 5;
    public int BasicAuthenticationLockoutSeconds { get; set; } = 300;
}