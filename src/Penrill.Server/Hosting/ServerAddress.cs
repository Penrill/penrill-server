using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Penrill.Api.Hosting;
using QRCoder;

namespace Penrill.Server.Hosting;

/// <summary>
/// How people reach this server. <see cref="Local"/> listens on loopback over plain HTTP (development,
/// or behind a reverse proxy on the same machine); <see cref="Lan"/> listens on every interface with
/// the pinned self-signed certificate.
/// </summary>
public enum AddressMode { Local, Lan }

public sealed record NetworkEndpoint(string Label, string Kind, string Origin, bool DefaultEnabled);

public sealed class ServerAddress(AddressMode mode, string localOrigin, int port, ServerCertificate? certificate) : IPublicAddress
{
    private volatile string? customPublicDomain;
    private volatile string? customPublicProvider;

    public AddressMode Mode => mode;

    public int Port => port;

    public ServerCertificate? Certificate => certificate;

    public string? Pin => certificate?.Pin;

    public string? ShortPin => certificate?.ShortPin;

    public void SetCustomPublicDomain(string? domain, string? provider)
    {
        customPublicDomain = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim().TrimEnd('.').ToLowerInvariant();
        customPublicProvider = string.IsNullOrWhiteSpace(provider) ? null : provider.Trim().ToLowerInvariant();
    }

    public bool IsTrustedHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var clean = host.Trim().Split(':')[0].Trim('[', ']').TrimEnd('.');
        if (certificate is not null && certificate.IsTrustedHost(clean))
            return true;
        if (customPublicProvider == "tailscale-funnel" &&
            !string.IsNullOrWhiteSpace(customPublicDomain) &&
            string.Equals(clean, customPublicDomain, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public bool IsTrustedOrigin(string? origin) =>
        !string.IsNullOrWhiteSpace(origin) &&
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        IsTrustedHost(uri.Host);

    public string? PinFor(string? origin) =>
        IsTrustedOrigin(origin) ? null : Pin;

    public string? PinFor(HttpRequest request) =>
        IsTrustedHost(EffectiveHost(request)) ? null : Pin;

    public string? ShortPinFor(HttpRequest request) =>
        IsTrustedHost(EffectiveHost(request)) ? null : ShortPin;

    public string? PublicDomainOrigin
    {
        get
        {
            if (mode != AddressMode.Lan) return null;
            if (customPublicProvider == "tailscale-funnel" && !string.IsNullOrWhiteSpace(customPublicDomain))
                return $"https://{customPublicDomain}";
            var domain = certificate?.TrustedDomain ?? customPublicDomain;
            if (!string.IsNullOrWhiteSpace(domain))
                return port == 443 ? $"https://{domain}" : $"https://{domain}:{port}";
            return null;
        }
    }

    /// <summary>The origin to print when there is no request to go by: the public domain if configured, otherwise the primary LAN address.</summary>
    public string DefaultOrigin => PublicDomainOrigin ?? (mode == AddressMode.Lan ? $"https://{Bracket(PrimaryAddress())}:{port}" : localOrigin);

    /// <summary>Whatever name the visitor used to get here works for them, including X-Forwarded-Host from a local tunnel/proxy such as Tailscale Funnel.</summary>
    public string Origin(HttpRequest request)
    {
        if (mode == AddressMode.Local) return $"{request.Scheme}://{request.Host}";
        var effectiveHost = EffectiveHost(request);
        var hostOnly = effectiveHost.Split(':')[0].Trim('[', ']');
        return hostOnly is "localhost" || IPAddress.TryParse(hostOnly, out var ip) && IPAddress.IsLoopback(ip)
            ? DefaultOrigin
            : $"https://{effectiveHost}";
    }

    private static string EffectiveHost(HttpRequest request)
    {
        var remoteIp = request.HttpContext.Connection.RemoteIpAddress;
        var hostOnly = request.Host.Host.Trim('[', ']');
        var isLocalProxy = (remoteIp is not null && IPAddress.IsLoopback(remoteIp)) ||
                           hostOnly is "localhost" ||
                           (IPAddress.TryParse(hostOnly, out var hostIp) && IPAddress.IsLoopback(hostIp));
        if (isLocalProxy && request.Headers.TryGetValue("X-Forwarded-Host", out var fwd) && ! string.IsNullOrWhiteSpace(fwd))
        {
            var first = fwd.ToString().Split(',')[0].Trim();
            if (first.Length > 0) return first;
        }
        return request.Host.Value ?? "";
    }

    public void RefreshCertificate(IEnumerable<string> origins) => certificate?.UpdateExtraOrigins(origins);

    public IReadOnlyList<NetworkEndpoint> DiscoverEndpoints()
    {
        if (mode == AddressMode.Local)
            return [new NetworkEndpoint("Local loopback", "local", localOrigin, true)];

        var list = new List<NetworkEndpoint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string label, string kind, string origin, bool defaultEnabled)
        {
            if (seen.Add(origin))
                list.Add(new NetworkEndpoint(label, kind, origin, defaultEnabled));
        }

        if (PublicDomainOrigin is { } domainOrigin)
            Add("Public Domain (SSL)", "domain", domainOrigin, true);

        var primaryIp = PrimaryAddress();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            var name = nic.Name;
            if (IsContainerBridge(name)) continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var ip = unicast.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
                var origin = $"https://{ip}:{port}";
                if (IsTailscale(name, ip))
                    Add($"Tailscale ({name})", "tailscale", origin, true);
                else if (IsVpnInterface(name))
                    Add($"VPN ({name})", "vpn", origin, true);
                else
                    Add($"LAN ({name})", "lan", origin, true);
            }
        }

        // Ensure primary LAN IP is first among LAN/VPN interfaces (right after Public Domain if configured).
        var targetIndex = PublicDomainOrigin is not null ? 1 : 0;
        var primaryOrigin = $"https://{Bracket(primaryIp)}:{port}";
        var primaryIndex = list.FindIndex(e => string.Equals(e.Origin, primaryOrigin, StringComparison.OrdinalIgnoreCase));
        if (primaryIndex > targetIndex)
        {
            var item = list[primaryIndex];
            list.RemoveAt(primaryIndex);
            list.Insert(Math.Min(targetIndex, list.Count), item);
        }
        else if (primaryIndex < 0 && !IPAddress.IsLoopback(primaryIp))
        {
            Add("LAN", "lan", primaryOrigin, true);
        }

        if (TryGetTailscaleDnsName() is { } tsDns)
            Add("Tailscale MagicDNS", "tailscale-dns", $"https://{tsDns}:{port}", true);

        var host = Dns.GetHostName();
        if (!string.IsNullOrWhiteSpace(host))
        {
            var mdns = host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? host : host + ".local";
            Add("Local mDNS (.local)", "mdns", $"https://{mdns}:{port}", false);
        }

        if (list.Count == 0)
            Add("Local", "lan", DefaultOrigin, true);

        return list;
    }

    public IReadOnlyList<NetworkEndpoint> ActiveEndpoints(string? savedOriginsCsv)
    {
        var discovered = DiscoverEndpoints();
        if (string.IsNullOrWhiteSpace(savedOriginsCsv))
        {
            var defaults = discovered.Where(d => d.DefaultEnabled).ToList();
            return defaults.Count > 0 ? defaults : [discovered[0]];
        }

        var byOrigin = discovered.ToDictionary(d => d.Origin, StringComparer.OrdinalIgnoreCase);
        var active = new List<NetworkEndpoint>();
        foreach (var raw in savedOriginsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var origin = NormalizeOrigin(raw, port, mode == AddressMode.Lan ? "https" : "http");
            if (origin is null) continue;
            if (byOrigin.TryGetValue(origin, out var match))
                active.Add(match);
            else
                active.Add(new NetworkEndpoint(ClassifyCustomOrigin(origin), "custom", origin, true));
        }

        if (PublicDomainOrigin is { } domOrigin)
        {
            var existingIdx = active.FindIndex(a => string.Equals(a.Origin, domOrigin, StringComparison.OrdinalIgnoreCase));
            var endpoint = byOrigin.TryGetValue(domOrigin, out var dMatch)
                ? dMatch
                : new NetworkEndpoint("Public Domain (SSL)", "domain", domOrigin, true);
            if (existingIdx > 0)
            {
                active.RemoveAt(existingIdx);
                active.Insert(0, endpoint);
            }
            else if (existingIdx < 0)
            {
                active.Insert(0, endpoint);
            }
        }

        return active.Count > 0 ? active : [discovered[0]];
    }

    public static string? NormalizeOrigin(string? input, int defaultPort, string defaultScheme = "https")
    {
        var clean = (input ?? "").Trim().TrimEnd('/');
        if (clean.Length == 0) return null;
        if (!clean.Contains("://", StringComparison.Ordinal))
            clean = $"{defaultScheme}://{clean}";
        if (!Uri.TryCreate(clean, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return null;
        var hasExplicitPort = clean.Split("://")[1].Contains(':', StringComparison.Ordinal);
        var finalPort = hasExplicitPort ? uri.Port : defaultPort;
        var host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.Host.Trim('[', ']')}]" : uri.Host;
        return finalPort is 80 or 443 && ((uri.Scheme == "http" && finalPort == 80) || (uri.Scheme == "https" && finalPort == 443))
            ? $"{uri.Scheme}://{host}"
            : $"{uri.Scheme}://{host}:{finalPort}";
    }

    private static string ClassifyCustomOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return "Custom";
        if (uri.Host.EndsWith(".duckdns.org", StringComparison.OrdinalIgnoreCase)) return "Public Domain (DuckDNS)";
        if (uri.Host.EndsWith(".dedyn.io", StringComparison.OrdinalIgnoreCase)) return "Public Domain (deSEC)";
        if (uri.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)) return "Tailscale MagicDNS";
        if (uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return "Local mDNS (.local)";
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IsTailscale("", ip)) return "Tailscale";
        return "Custom";
    }

    private static bool IsTailscale(string nicName, IPAddress ip)
    {
        if (nicName.StartsWith("tailscale", StringComparison.OrdinalIgnoreCase)) return true;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = ip.GetAddressBytes();
        // Tailscale uses CGNAT range 100.64.0.0/10
        return bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    private static bool IsVpnInterface(string nicName) =>
        nicName.StartsWith("wg", StringComparison.OrdinalIgnoreCase) ||
        nicName.StartsWith("tun", StringComparison.OrdinalIgnoreCase) ||
        nicName.StartsWith("tap", StringComparison.OrdinalIgnoreCase) ||
        nicName.StartsWith("zt", StringComparison.OrdinalIgnoreCase) ||
        nicName.StartsWith("nebula", StringComparison.OrdinalIgnoreCase);

    private static bool IsContainerBridge(string nicName) =>
        nicName.StartsWith("docker", StringComparison.OrdinalIgnoreCase) ||
        nicName.StartsWith("br-", StringComparison.OrdinalIgnoreCase) ||
        nicName.StartsWith("veth", StringComparison.OrdinalIgnoreCase) ||
        nicName.StartsWith("virbr", StringComparison.OrdinalIgnoreCase) ||
        nicName.StartsWith("cni", StringComparison.OrdinalIgnoreCase);

    public static string? TryGetTailscaleDnsName()
    {
        try
        {
            var bin = File.Exists("/usr/bin/tailscale") ? "/usr/bin/tailscale"
                : File.Exists("/usr/local/bin/tailscale") ? "/usr/local/bin/tailscale"
                : null;
            if (bin is null) return null;
            var psi = new ProcessStartInfo(bin, ["status", "--json"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(1200) || proc.ExitCode != 0) return null;
            var json = JsonNode.Parse(output)?.AsObject();
            var dns = (string?)json?["Self"]?["DNSName"];
            dns = dns?.Trim().TrimEnd('.');
            return !string.IsNullOrWhiteSpace(dns) ? dns : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The address of the interface that holds the default route. No packet is sent: connecting a UDP socket only picks a route.</summary>
    public static IPAddress PrimaryAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9));
            if (socket.LocalEndPoint is IPEndPoint { Address: var address } && !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any)) return address;
        }
        catch (SocketException)
        {
        }
        return ServerCertificate.LocalAddresses().FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? IPAddress.Loopback;
    }

    private static string Bracket(IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
}

/// <summary>
/// The link that connects Penrill to this server. It goes to the connect page on penrill.com, which
/// knows what the visitor has: it opens the app, offers Penrill in the browser, or offers a download.
/// The server only knows its own address. The address travels in the fragment, so it never reaches
/// penrill.com. Development points <c>PENRILL_CONNECT_PAGE</c> at a local web editor instead.
/// </summary>
public sealed class ConnectLinks(string page)
{
    public const string DefaultPage = "https://penrill.com/connect";

    public string For(string origin, string? pin) =>
        page.Split('#')[0] + "#server=" + Uri.EscapeDataString(origin) + (pin is null ? "" : "&pin=" + Uri.EscapeDataString(pin));
}

public static class Qr
{
    public static string Svg(string text)
    {
        using var data = QRCodeGenerator.GenerateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(4, "#18181b", "#ffffff", drawQuietZones: true, SvgQRCode.SizingMode.ViewBoxAttribute);
    }

    /// <summary>Two modules per character with half blocks, dark on light, so it scans from a dark terminal too.</summary>
    public static string Terminal(string text)
    {
        using var data = QRCodeGenerator.GenerateQrCode(text, QRCodeGenerator.ECCLevel.L);
        var matrix = data.ModuleMatrix;
        var size = matrix.Count;
        bool Dark(int row, int column) => row < size && matrix[row][column];
        var output = new StringBuilder();
        for (var row = 0; row < size; row += 2)
        {
            for (var column = 0; column < size; column++)
                output.Append((Dark(row, column), Dark(row + 1, column)) switch
                {
                    (false, false) => '█',
                    (true, false) => '▄',
                    (false, true) => '▀',
                    _ => ' '
                });
            output.Append('\n');
        }
        return output.ToString();
    }
}
