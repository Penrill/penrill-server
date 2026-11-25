using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Web.Hosting;

/// <summary>
/// The server's TLS certificates:
/// 1. Its own self-signed certificate for LAN/VPN/Tailscale, trusted by its public-key SHA-256 pin.
/// 2. An optional publicly trusted CA certificate (e.g. Let's Encrypt via deSEC DNS-01 or custom PEM)
///    selected automatically via TLS SNI when visitors connect through the configured public domain.
/// </summary>
public sealed class ServerCertificate
{
    private static readonly TimeSpan Validity = TimeSpan.FromDays(3650);
    private readonly string tlsDirectory;
    private readonly string keyPath;
    private readonly string hostsPath;
    private readonly string domainNamePath;
    private readonly string domainChainPath;
    private readonly string domainKeyPath;
    private readonly Lock sync = new();
    private volatile X509Certificate2 certificate;
    private volatile X509Certificate2? trustedCertificate;
    private volatile string? trustedDomain;

    public ServerCertificate(string dataDirectory)
    {
        tlsDirectory = Path.Combine(dataDirectory, "tls");
        keyPath = Path.Combine(tlsDirectory, "server.key");
        hostsPath = Path.Combine(tlsDirectory, "hosts.txt");
        domainNamePath = Path.Combine(tlsDirectory, "domain.txt");
        domainChainPath = Path.Combine(tlsDirectory, "domain-fullchain.pem");
        domainKeyPath = Path.Combine(tlsDirectory, "domain.key");
        Directory.CreateDirectory(tlsDirectory);
        using var key = LoadOrCreateKey();
        certificate = Create(key, ReadSavedHosts());
        Pin = PinOf(key.ExportSubjectPublicKeyInfo());
        TryLoadTrustedCertificateFromDisk();
    }

    public string TlsDirectory => tlsDirectory;

    public X509Certificate2 Certificate => certificate;

    public X509Certificate2? TrustedCertificate => trustedCertificate;

    public string? TrustedDomain => trustedDomain;

    public string? TrustedIssuer => trustedCertificate?.GetNameInfo(X509NameType.SimpleName, forIssuer: true);

    public DateTimeOffset? TrustedExpires => trustedCertificate is { } cert ? new DateTimeOffset(cert.NotAfter.ToUniversalTime()) : null;

    /// <summary><c>sha256/&lt;base64&gt;</c>, the form used in connect links and by the editor.</summary>
    public string Pin { get; }

    /// <summary>The first bytes of the pin in hex groups, short enough to compare by eye: <c>3F9A-12C4-77B0</c>.</summary>
    public string ShortPin => ShortForm(Pin);

    public static string PinOf(byte[] subjectPublicKeyInfo) => "sha256/" + Convert.ToBase64String(SHA256.HashData(subjectPublicKeyInfo));

    public static string ShortForm(string pin)
    {
        var hash = Convert.FromBase64String(pin["sha256/".Length..]);
        var hex = Convert.ToHexString(hash, 0, 6);
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}";
    }

    /// <summary>Selects the publicly trusted domain certificate when TLS SNI matches the public domain, or the self-signed certificate otherwise.</summary>
    public X509Certificate2 SelectCertificate(string? sniHost)
    {
        if (trustedCertificate is { } trusted && IsTrustedHost(sniHost))
            return trusted;
        return certificate;
    }

    public bool IsTrustedHost(string? host)
    {
        if (trustedCertificate is null || string.IsNullOrWhiteSpace(trustedDomain) || string.IsNullOrWhiteSpace(host))
            return false;
        var clean = host.Trim().Trim('[', ']').TrimEnd('.');
        return string.Equals(clean, trustedDomain, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsTrustedOrigin(string? origin) =>
        !string.IsNullOrWhiteSpace(origin) &&
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        IsTrustedHost(uri.Host);

    public bool NeedsTrustedRenewal(string domain, TimeSpan renewBeforeExpiry)
    {
        if (trustedCertificate is not { } cert || !IsTrustedHost(domain))
            return true;
        return cert.NotAfter.ToUniversalTime() <= DateTime.UtcNow + renewBeforeExpiry;
    }

    /// <summary>Installs and persists a publicly trusted certificate (Let's Encrypt or custom PEM) for <paramref name="domain"/>.</summary>
    public void SetTrustedDomainCertificate(string domain, string fullChainPem, string privateKeyPem)
    {
        var cleanDomain = domain.Trim().TrimEnd('.').ToLowerInvariant();
        var loaded = BuildCertificateWithChain(fullChainPem, privateKeyPem);
        lock (sync)
        {
            File.WriteAllText(domainNamePath, cleanDomain);
            File.WriteAllText(domainChainPath, fullChainPem);
            PrivateFile.Write(domainKeyPath, privateKeyPem);
            trustedDomain = cleanDomain;
            trustedCertificate = loaded;
        }
    }

    public void ClearTrustedDomainCertificate()
    {
        lock (sync)
        {
            trustedCertificate = null;
            trustedDomain = null;
            if (File.Exists(domainNamePath)) File.Delete(domainNamePath);
            if (File.Exists(domainChainPath)) File.Delete(domainChainPath);
            if (File.Exists(domainKeyPath)) File.Delete(domainKeyPath);
        }
    }

    private void TryLoadTrustedCertificateFromDisk()
    {
        try
        {
            if (!File.Exists(domainNamePath) || !File.Exists(domainChainPath) || !File.Exists(domainKeyPath))
                return;
            var domain = File.ReadAllText(domainNamePath).Trim().ToLowerInvariant();
            var chainPem = File.ReadAllText(domainChainPath);
            var keyPem = File.ReadAllText(domainKeyPath);
            if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(chainPem) || string.IsNullOrWhiteSpace(keyPem))
                return;
            trustedCertificate = BuildCertificateWithChain(chainPem, keyPem);
            trustedDomain = domain;
        }
        catch
        {
            // Corrupted or expired custom cert files on disk should not prevent the server from starting.
        }
    }

    private static X509Certificate2 BuildCertificateWithChain(string fullChainPem, string privateKeyPem)
    {
        var chain = new X509Certificate2Collection();
        chain.ImportFromPem(fullChainPem);
        if (chain.Count == 0)
            throw new InvalidOperationException("No certificate found in PEM chain.");

        // Install intermediate CA certificates into the user's CA store so OpenSSL on Linux sends the full chain in TLS handshakes.
        if (chain.Count > 1)
        {
            try
            {
                using var caStore = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
                caStore.Open(OpenFlags.ReadWrite);
                for (var i = 1; i < chain.Count; i++)
                    caStore.Add(chain[i]);
            }
            catch
            {
                // Best-effort intermediate registration.
            }
        }

        using var leafWithKey = X509Certificate2.CreateFromPem(fullChainPem, privateKeyPem);
        return X509CertificateLoader.LoadPkcs12(leafWithKey.Export(X509ContentType.Pfx), null);
    }

    /// <summary>Updates the self-signed certificate's Subject Alternative Names with custom origins/hosts while keeping the same key and pin.</summary>
    public void UpdateExtraOrigins(IEnumerable<string> origins)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in origins)
        {
            if (Uri.TryCreate(item.Trim(), UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
                hosts.Add(uri.Host.Trim('[', ']'));
            else if (!string.IsNullOrWhiteSpace(item))
                hosts.Add(item.Trim().Trim('[', ']'));
        }
        lock (sync)
        {
            File.WriteAllLines(hostsPath, hosts);
            using var key = LoadOrCreateKey();
            certificate = Create(key, hosts);
        }
    }

    private ECDsa LoadOrCreateKey()
    {
        var key = ECDsa.Create();
        if (File.Exists(keyPath))
        {
            key.ImportFromPem(File.ReadAllText(keyPath));
        }
        else
        {
            key.GenerateKey(ECCurve.NamedCurves.nistP256);
            PrivateFile.Write(keyPath, key.ExportPkcs8PrivateKeyPem());
        }
        return key;
    }

    private IReadOnlyList<string> ReadSavedHosts() =>
        File.Exists(hostsPath)
            ? File.ReadAllLines(hostsPath).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList()
            : [];

    /// <summary>A fresh certificate on every start: names and addresses follow the machine, the key and so the pin stay.</summary>
    private static X509Certificate2 Create(ECDsa key, IEnumerable<string> extraHosts)
    {
        var host = Dns.GetHostName();
        var request = new CertificateRequest($"CN={host}, O=Penrill Server", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        var dnsNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            host,
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? host : host + ".local",
            "localhost"
        };
        var ipAddresses = new HashSet<IPAddress>(LocalAddresses()) { IPAddress.Loopback };

        if (ServerAddress.TryGetTailscaleDnsName() is { } tsDns)
            dnsNames.Add(tsDns);

        foreach (var extra in extraHosts)
        {
            if (IPAddress.TryParse(extra, out var extraIp)) ipAddresses.Add(extraIp);
            else if (!string.IsNullOrWhiteSpace(extra)) dnsNames.Add(extra);
        }

        foreach (var name in dnsNames) names.AddDnsName(name);
        foreach (var address in ipAddresses) names.AddIpAddress(address);

        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddDays(-1), now + Validity);
        // Kestrel on Linux needs the key in an exportable store-backed form; a PFX round trip gives that everywhere.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    public static IEnumerable<IPAddress> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork || (address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6LinkLocal))
            .Distinct();
}
