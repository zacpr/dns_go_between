using System.Net;
using System.Net.Sockets;

namespace DnsGoBetween.Api.Security;

public sealed class FileIpAccessPolicy
{
    private static readonly TimeSpan ReloadInterval = TimeSpan.FromSeconds(15);

    private readonly ILogger<FileIpAccessPolicy> _logger;
    private readonly string _installDir;
    private readonly object _gate = new();

    private DateTimeOffset _nextReloadAt = DateTimeOffset.MinValue;
    private IReadOnlyList<IpRangeRule> _whitelist = [];
    private IReadOnlyList<IpRangeRule> _blacklist = [];
    private bool _whitelistFilePresent;
    private bool _blacklistFilePresent;

    public FileIpAccessPolicy(ILogger<FileIpAccessPolicy> logger)
    {
        _logger = logger;
        _installDir = AppContext.BaseDirectory;
    }

    public bool IsAllowed(IPAddress? remoteIp, out string reason)
    {
        EnsureLoaded();

        if (remoteIp is null)
        {
            reason = "Remote IP address was not available.";
            return false;
        }

        var normalized = NormalizeAddress(remoteIp);

        // Whitelist precedence: when active, only whitelisted addresses are allowed,
        // and blacklist is ignored for access decisions.
        if (_whitelistFilePresent && _whitelist.Count > 0)
        {
            if (_whitelist.Any(rule => rule.Contains(normalized)))
            {
                reason = string.Empty;
                return true;
            }

            reason = $"IP '{normalized}' is not in ipwhitelist.txt.";
            return false;
        }

        if (_blacklist.Count > 0 && _blacklist.Any(rule => rule.Contains(normalized)))
        {
            reason = $"IP '{normalized}' is blocked by ipblacklist.txt.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void EnsureLoaded()
    {
        if (DateTimeOffset.UtcNow < _nextReloadAt)
        {
            return;
        }

        lock (_gate)
        {
            if (DateTimeOffset.UtcNow < _nextReloadAt)
            {
                return;
            }

            LoadRules();
            _nextReloadAt = DateTimeOffset.UtcNow.Add(ReloadInterval);
        }
    }

    private void LoadRules()
    {
        var whitelistPath = Path.Combine(_installDir, "ipwhitelist.txt");
        var blacklistPath = Path.Combine(_installDir, "ipblacklist.txt");

        _whitelistFilePresent = File.Exists(whitelistPath);
        _blacklistFilePresent = File.Exists(blacklistPath);
        _whitelist = ParseRulesFromFile(whitelistPath);
        _blacklist = ParseRulesFromFile(blacklistPath);

        if (_whitelistFilePresent && _whitelist.Count > 0 && _blacklistFilePresent && _blacklist.Count > 0)
        {
            _logger.LogWarning(
                "Both ipwhitelist.txt and ipblacklist.txt contain active rules. Use one list at a time; whitelist takes precedence.");
        }

        _logger.LogInformation(
            "IP access policy loaded. whitelistFilePresent={WhitelistFilePresent}, whitelistRules={WhitelistRules}, blacklistFilePresent={BlacklistFilePresent}, blacklistRules={BlacklistRules}",
            _whitelistFilePresent,
            _whitelist.Count,
            _blacklistFilePresent,
            _blacklist.Count);
    }

    private IReadOnlyList<IpRangeRule> ParseRulesFromFile(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var rules = new List<IpRangeRule>();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var content = sr.ReadToEnd();

        foreach (var rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine;
            var commentIndex = line.IndexOf('#');
            if (commentIndex >= 0)
            {
                line = line[..commentIndex];
            }

            foreach (var token in line.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryParseRule(token.Trim(), out var rule))
                {
                    rules.Add(rule);
                }
                else
                {
                    _logger.LogWarning("Invalid IP rule '{Rule}' in {Path}. Expected IP or CIDR.", token.Trim(), path);
                }
            }
        }

        return rules;
    }

    private static bool TryParseRule(string value, out IpRangeRule rule)
    {
        rule = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var slash = value.IndexOf('/');
        if (slash < 0)
        {
            if (!IPAddress.TryParse(value, out var singleIp))
            {
                return false;
            }

            var bits = singleIp.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            rule = new IpRangeRule(NormalizeAddress(singleIp), bits);
            return true;
        }

        var ipPart = value[..slash].Trim();
        var prefixPart = value[(slash + 1)..].Trim();

        if (!IPAddress.TryParse(ipPart, out var ip))
        {
            return false;
        }

        var normalizedIp = NormalizeAddress(ip);
        var maxBits = normalizedIp.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (!int.TryParse(prefixPart, out var prefix) || prefix < 0 || prefix > maxBits)
        {
            return false;
        }

        rule = new IpRangeRule(normalizedIp, prefix);
        return true;
    }

    private static IPAddress NormalizeAddress(IPAddress ip)
    {
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    private readonly struct IpRangeRule
    {
        private readonly byte[] _networkBytes;

        public IpRangeRule(IPAddress network, int prefixLength)
        {
            AddressFamily = network.AddressFamily;
            PrefixLength = prefixLength;
            _networkBytes = network.GetAddressBytes();
            ApplyMask(_networkBytes, PrefixLength);
        }

        public AddressFamily AddressFamily { get; }
        public int PrefixLength { get; }

        public bool Contains(IPAddress ip)
        {
            if (ip.AddressFamily != AddressFamily)
            {
                return false;
            }

            var candidate = ip.GetAddressBytes();
            ApplyMask(candidate, PrefixLength);
            return candidate.SequenceEqual(_networkBytes);
        }

        private static void ApplyMask(byte[] bytes, int prefixLength)
        {
            var fullBytes = prefixLength / 8;
            var remainingBits = prefixLength % 8;

            for (var i = fullBytes + (remainingBits > 0 ? 1 : 0); i < bytes.Length; i++)
            {
                bytes[i] = 0;
            }

            if (remainingBits > 0 && fullBytes < bytes.Length)
            {
                var mask = (byte)(0xFF << (8 - remainingBits));
                bytes[fullBytes] &= mask;
            }
        }
    }
}
