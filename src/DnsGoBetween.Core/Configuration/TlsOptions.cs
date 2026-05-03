namespace DnsGoBetween.Core.Configuration;

public sealed class TlsOptions
{
    public const string SectionName = "Tls";

    public int HttpsPort { get; set; } = 6790;
    public bool EnableHttp { get; set; }
    public int HttpPort { get; set; } = 0;
    public bool RedirectHttpToHttps { get; set; }
    public bool AutoSelectMachineCertificate { get; set; } = true;

    public TlsCertificateOptions Certificate { get; set; } = new();
}

public sealed class TlsCertificateOptions
{
    public string StoreName { get; set; } = "My";
    public string StoreLocation { get; set; } = "LocalMachine";
    public string? Thumbprint { get; set; }
    public string? Subject { get; set; }

    public string? PfxPath { get; set; }
    public string? PfxPassword { get; set; }
}
