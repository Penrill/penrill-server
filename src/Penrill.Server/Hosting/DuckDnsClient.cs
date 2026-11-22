using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Penrill.Server.Hosting;

/// <summary>
/// Client for DuckDNS (<c>https://www.duckdns.org</c> / <c>*.duckdns.org</c>).
/// Supports automatic DDNS IPv4 updates and ACME <c>dns-01</c> TXT record provisioning for Let's Encrypt.
/// </summary>
public sealed class DuckDnsClient
{
    private const string UpdateUrl = "https://www.duckdns.org/update";
    private readonly HttpClient http;

    public DuckDnsClient(HttpClient? httpClient = null)
    {
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PenrillServer/1.0");
    }

    /// <summary>
    /// Normalizes a DuckDNS domain or subdomain input (e.g. <c>myboard</c> or <c>myboard.duckdns.org</c>)
    /// into <c>(Subdomain: "myboard", FullDomain: "myboard.duckdns.org")</c>.
    /// </summary>
    public static (string Subdomain, string FullDomain)? NormalizeDomain(string? raw)
    {
        var clean = (raw ?? "").Trim().ToLowerInvariant();
        if (clean.Length == 0) return null;
        if (clean.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(clean, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
                return null;
            clean = uri.Host;
        }
        else if (clean.Contains(':', StringComparison.Ordinal))
        {
            clean = clean.Split(':')[0];
        }
        clean = clean.TrimEnd('.', '/');
        if (clean.EndsWith(".duckdns.org", StringComparison.OrdinalIgnoreCase))
            clean = clean[..^".duckdns.org".Length];

        if (clean.Length == 0 || clean.Contains('.', StringComparison.Ordinal))
            return null;

        if (!clean.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-'))
            return null;

        if (clean.StartsWith('-') || clean.EndsWith('-'))
            return null;

        return (clean, $"{clean}.duckdns.org");
    }

    public async Task<(bool Ok, string? Error)> UpdateAddressRecordAsync(
        string token, string domainOrSubdomain, string ipv4, CancellationToken cancel)
    {
        if (NormalizeDomain(domainOrSubdomain) is not { } norm)
            return (false, $"'{domainOrSubdomain}' is not a valid *.duckdns.org domain.");

        try
        {
            var url = $"{UpdateUrl}?domains={Uri.EscapeDataString(norm.Subdomain)}&token={Uri.EscapeDataString(token.Trim())}&ip={Uri.EscapeDataString(ipv4)}&verbose=true";
            var response = (await http.GetStringAsync(url, cancel)).Trim();
            if (response.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                return (true, null);

            return (false, $"DuckDNS returned '{response}'. Make sure '{norm.FullDomain}' is added to your account on https://www.duckdns.org and your token is correct.");
        }
        catch (Exception ex)
        {
            return (false, $"Could not reach duckdns.org: {ex.Message}");
        }
    }

    public async Task<(bool Ok, string? Error)> SetAcmeTxtRecordAsync(
        string token, string domainOrSubdomain, string txtDigest, CancellationToken cancel)
    {
        if (NormalizeDomain(domainOrSubdomain) is not { } norm)
            return (false, $"'{domainOrSubdomain}' is not a valid *.duckdns.org domain.");

        try
        {
            var url = $"{UpdateUrl}?domains={Uri.EscapeDataString(norm.Subdomain)}&token={Uri.EscapeDataString(token.Trim())}&txt={Uri.EscapeDataString(txtDigest)}&verbose=true";
            var response = (await http.GetStringAsync(url, cancel)).Trim();
            if (!response.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                return (false, $"Failed to set DuckDNS TXT record ('{response}'). Verify your DuckDNS token and domain.");

            await WaitForTxtPropagationAsync($"_acme-challenge.{norm.FullDomain}", txtDigest, cancel);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to set DuckDNS TXT record: {ex.Message}");
        }
    }

    public async Task ClearAcmeTxtRecordAsync(string token, string domainOrSubdomain, CancellationToken cancel)
    {
        if (NormalizeDomain(domainOrSubdomain) is not { } norm) return;
        try
        {
            var url = $"{UpdateUrl}?domains={Uri.EscapeDataString(norm.Subdomain)}&token={Uri.EscapeDataString(token.Trim())}&txt=&clear=true";
            await http.GetStringAsync(url, cancel);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static async Task WaitForTxtPropagationAsync(string fqdn, string expectedTxt, CancellationToken cancel)
    {
        var resolvers = new List<IPAddress>();
        try
        {
            foreach (var host in new[] { "ns1.duckdns.org", "ns2.duckdns.org", "1.1.1.1", "8.8.8.8" })
            {
                try
                {
                    if (IPAddress.TryParse(host, out var literal))
                    {
                        resolvers.Add(literal);
                    }
                    else
                    {
                        var resolved = (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancel)).FirstOrDefault();
                        if (resolved is not null) resolvers.Add(resolved);
                    }
                }
                catch
                {
                    // Ignore single resolver lookup failure.
                }
            }
        }
        catch
        {
            // Fall through.
        }

        if (resolvers.Count == 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(8), cancel);
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline && !cancel.IsCancellationRequested)
        {
            var hits = 0;
            foreach (var ns in resolvers)
            {
                if (await QueryDnsTxtContainsAsync(ns, fqdn, expectedTxt, cancel))
                    hits++;
            }
            if (hits >= Math.Min(2, resolvers.Count))
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancel);
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancel);
        }
    }

    private static async Task<bool> QueryDnsTxtContainsAsync(IPAddress nameserver, string fqdn, string expectedTxt, CancellationToken cancel)
    {
        try
        {
            var query = BuildDnsTxtQuery(fqdn);
            using var udp = new UdpClient();
            await udp.SendAsync(query, new IPEndPoint(nameserver, 53), cancel);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var result = await udp.ReceiveAsync(cts.Token);
            var ascii = Encoding.ASCII.GetString(result.Buffer);
            return ascii.Contains(expectedTxt, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] BuildDnsTxtQuery(string fqdn)
    {
        using var ms = new MemoryStream();
        ms.Write([0x24, 0x68, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        foreach (var label in fqdn.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0x00);
        ms.Write([0x00, 0x10]); // QTYPE = TXT (16)
        ms.Write([0x00, 0x01]); // QCLASS = IN (1)
        return ms.ToArray();
    }
}
