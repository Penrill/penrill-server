using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Penrill.Server.Hosting;

/// <summary>
/// The server's own TLS certificate for "this network only": self-signed, created on first start and
/// kept in the data directory. Editors trust it by its pin, the SHA-256 of the public key, so a
/// renewed certificate with the same key keeps working.
/// </summary>
public sealed class ServerCertificate
{
    private static readonly TimeSpan Validity = TimeSpan.FromDays(3650);

    public ServerCertificate(string dataDirectory)
    {
        var directory = Path.Combine(dataDirectory, "tls");
        var keyPath = Path.Combine(directory, "server.key");
        Directory.CreateDirectory(directory);
        using var key = ECDsa.Create();
        if (File.Exists(keyPath)) key.ImportFromPem(File.ReadAllText(keyPath));
        else
        {
            key.GenerateKey(ECCurve.NamedCurves.nistP256);
            PrivateFile.Write(keyPath, key.ExportPkcs8PrivateKeyPem());
        }
        Certificate = Create(key);
        Pin = PinOf(key.ExportSubjectPublicKeyInfo());
    }

    public X509Certificate2 Certificate { get; }

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

    /// <summary>A fresh certificate on every start: names and addresses follow the machine, the key and so the pin stay.</summary>
    private static X509Certificate2 Create(ECDsa key)
    {
        var host = Dns.GetHostName();
        var request = new CertificateRequest($"CN={host}, O=Penrill Server", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        foreach (var name in new[] { host, host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? host : host + ".local", "localhost" }.Distinct(StringComparer.OrdinalIgnoreCase)) names.AddDnsName(name);
        foreach (var address in LocalAddresses()) names.AddIpAddress(address);
        names.AddIpAddress(IPAddress.Loopback);
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
