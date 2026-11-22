using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Penrill.Server.Hosting;

/// <summary>
/// Client for the non-profit deSEC (<c>desec.io</c> / <c>*.dedyn.io</c>) DNS &amp; DDNS API.
/// Handles public IP detection, domain provisioning, DDNS updates, and ACME DNS-01 TXT records.
/// </summary>
public sealed class DesecClient
{
    private const string ApiBase = "https://desec.io/api/v1";
    private readonly HttpClient http;

    public DesecClient(HttpClient? httpClient = null)
    {
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PenrillServer/1.0");
    }

    /// <summary>
    /// Normalizes a domain input. If the user enters a bare label like <c>myboard</c>,
    /// appends <c>.dedyn.io</c> automatically (<c>myboard.dedyn.io</c>).
    /// </summary>
    public static string? NormalizeDomain(string? raw, bool defaultToDedyn = true)
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
        if (clean.Length == 0) return null;

        if (!clean.Contains('.', StringComparison.Ordinal))
        {
            if (!defaultToDedyn) return null;
            clean += ".dedyn.io";
        }

        if (Uri.CheckHostName(clean) != UriHostNameType.Dns || IPAddress.TryParse(clean, out _))
            return null;

        return clean;
    }

    public async Task<string?> DetectPublicIpv4Async(CancellationToken cancel)
    {
        foreach (var url in new[] { "https://checkipv4.dedyn.io/", "https://api.ipify.org/" })
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                var text = (await http.GetStringAsync(url, cts.Token)).Trim();
                if (IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                    return ip.ToString();
            }
            catch
            {
                // Try next provider.
            }
        }
        return null;
    }

    /// <summary>
    /// Signs in once with a deSEC email and password to create a dedicated scoped API token for Penrill Server,
    /// then logs out the temporary session so the password is never stored.
    /// </summary>
    public async Task<(string? Token, string? Error)> CreateTokenFromLoginAsync(
        string email, string password, string tokenName, CancellationToken cancel)
    {
        try
        {
            using var loginReq = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/auth/login/")
            {
                Content = JsonContent(new { email = email.Trim(), password })
            };
            using var loginResp = await http.SendAsync(loginReq, cancel);
            var loginBody = await loginResp.Content.ReadAsStringAsync(cancel);
            if (!loginResp.IsSuccessStatusCode)
                return (null, $"deSEC login failed ({(int)loginResp.StatusCode}): {ExtractError(loginBody)}");

            var loginJson = JsonNode.Parse(loginBody)?.AsObject();
            var sessionToken = (string?)loginJson?["token"];
            if (string.IsNullOrWhiteSpace(sessionToken))
                return (null, "deSEC login did not return a session token.");

            try
            {
                using var tokenReq = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/auth/tokens/")
                {
                    Content = JsonContent(new { name = tokenName, perm_create_domain = true })
                };
                tokenReq.Headers.Authorization = new AuthenticationHeaderValue("Token", sessionToken);
                using var tokenResp = await http.SendAsync(tokenReq, cancel);
                var tokenBody = await tokenResp.Content.ReadAsStringAsync(cancel);
                if (!tokenResp.IsSuccessStatusCode)
                    return (null, $"Could not create deSEC API token: {ExtractError(tokenBody)}");

                var tokenJson = JsonNode.Parse(tokenBody)?.AsObject();
                var permanentToken = (string?)tokenJson?["token"];
                return !string.IsNullOrWhiteSpace(permanentToken)
                    ? (permanentToken, null)
                    : (null, "deSEC did not return the new token value.");
            }
            finally
            {
                try
                {
                    using var logoutReq = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/auth/logout/");
                    logoutReq.Headers.Authorization = new AuthenticationHeaderValue("Token", sessionToken);
                    await http.SendAsync(logoutReq, cancel);
                }
                catch
                {
                    // Ignore logout cleanup failure.
                }
            }
        }
        catch (Exception ex)
        {
            return (null, $"Could not reach desec.io: {ex.Message}");
        }
    }

    /// <summary>Verifies that <paramref name="domain"/> exists in the user's deSEC account, creating it if needed.</summary>
    public async Task<(bool Ok, bool Created, string? Error)> EnsureDomainExistsAsync(
        string token, string domain, CancellationToken cancel)
    {
        try
        {
            using var getReq = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/domains/{domain}/");
            getReq.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
            using var getResp = await http.SendAsync(getReq, cancel);
            if (getResp.IsSuccessStatusCode)
                return (true, false, null);

            if (getResp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var err = await getResp.Content.ReadAsStringAsync(cancel);
                return (false, false, $"Invalid deSEC API token or domain '{domain}' belongs to another user ({ExtractError(err)}).");
            }

            if (getResp.StatusCode != HttpStatusCode.NotFound)
            {
                var err = await getResp.Content.ReadAsStringAsync(cancel);
                return (false, false, $"deSEC domain check failed ({(int)getResp.StatusCode}): {ExtractError(err)}");
            }

            using var createReq = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/domains/")
            {
                Content = JsonContent(new { name = domain })
            };
            createReq.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
            using var createResp = await http.SendAsync(createReq, cancel);
            var createBody = await createResp.Content.ReadAsStringAsync(cancel);
            if (createResp.IsSuccessStatusCode)
                return (true, true, null);

            return (false, false, $"Could not create domain '{domain}' on deSEC: {ExtractError(createBody)}");
        }
        catch (Exception ex)
        {
            return (false, false, $"Failed to communicate with deSEC API: {ex.Message}");
        }
    }

    /// <summary>Updates the root IPv4 <c>A</c> record for <paramref name="domain"/> on deSEC.</summary>
    public async Task<(bool Ok, string? Error)> UpdateAddressRecordAsync(
        string token, string domain, string ipv4, CancellationToken cancel)
    {
        try
        {
            // Try the dynamic DNS update endpoint first (sets a 60s TTL on *.dedyn.io).
            try
            {
                using var ddnsReq = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://update.dedyn.io/?hostname={Uri.EscapeDataString(domain)}&myipv4={Uri.EscapeDataString(ipv4)}");
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{domain}:{token}"));
                ddnsReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
                using var ddnsResp = await http.SendAsync(ddnsReq, cancel);
                var ddnsText = (await ddnsResp.Content.ReadAsStringAsync(cancel)).Trim();
                if (ddnsResp.IsSuccessStatusCode && ddnsText.StartsWith("good", StringComparison.OrdinalIgnoreCase))
                    return (true, null);
            }
            catch
            {
                // Fall back to RRsets REST API below.
            }

            var payload = new[]
            {
                new
                {
                    subname = "",
                    type = "A",
                    ttl = 3600,
                    records = new[] { ipv4 }
                }
            };
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{ApiBase}/domains/{domain}/rrsets/")
            {
                Content = JsonContent(payload)
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
            using var resp = await http.SendAsync(req, cancel);
            if (resp.IsSuccessStatusCode)
                return (true, null);

            var body = await resp.Content.ReadAsStringAsync(cancel);
            return (false, $"Could not update DNS A record on deSEC ({(int)resp.StatusCode}): {ExtractError(body)}");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to update DNS A record: {ex.Message}");
        }
    }

    /// <summary>Sets the <c>_acme-challenge</c> TXT record for Let's Encrypt DNS-01 validation.</summary>
    public async Task<(bool Ok, string? Error)> SetAcmeTxtRecordAsync(
        string token, string domain, string txtDigest, CancellationToken cancel)
    {
        var quoted = $"\"{txtDigest}\"";
        var payload = new[]
        {
            new
            {
                subname = "_acme-challenge",
                type = "TXT",
                ttl = 3600,
                records = new[] { quoted }
            }
        };
        using var req = new HttpRequestMessage(HttpMethod.Put, $"{ApiBase}/domains/{domain}/rrsets/")
        {
            Content = JsonContent(payload)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        using var resp = await http.SendAsync(req, cancel);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancel);
            return (false, $"Failed to set _acme-challenge TXT record on deSEC ({(int)resp.StatusCode}): {ExtractError(body)}");
        }

        // Wait for deSEC's authoritative nameservers (ns1.desec.io / ns2.desec.org) to serve the new TXT record.
        await WaitForAuthoritativeTxtPropagationAsync($"_acme-challenge.{domain}", txtDigest, cancel);
        return (true, null);
    }

    /// <summary>Removes the <c>_acme-challenge</c> TXT record after validation finishes.</summary>
    public async Task ClearAcmeTxtRecordAsync(string token, string domain, CancellationToken cancel)
    {
        try
        {
            var payload = new[]
            {
                new
                {
                    subname = "_acme-challenge",
                    type = "TXT",
                    ttl = 3600,
                    records = Array.Empty<string>()
                }
            };
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{ApiBase}/domains/{domain}/rrsets/")
            {
                Content = JsonContent(payload)
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
            await http.SendAsync(req, cancel);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static async Task WaitForAuthoritativeTxtPropagationAsync(string fqdn, string expectedTxt, CancellationToken cancel)
    {
        // Query both ns1.desec.io and ns2.desec.org over UDP port 53 until both return the expected TXT record (up to ~25s).
        IPAddress[] nameservers;
        try
        {
            var ns1 = (await Dns.GetHostAddressesAsync("ns1.desec.io", AddressFamily.InterNetwork, cancel)).FirstOrDefault();
            var ns2 = (await Dns.GetHostAddressesAsync("ns2.desec.org", AddressFamily.InterNetwork, cancel)).FirstOrDefault();
            nameservers = new[] { ns1, ns2 }.Where(ip => ip is not null).Select(ip => ip!).ToArray();
        }
        catch
        {
            nameservers = [];
        }

        if (nameservers.Length == 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(6), cancel);
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline && !cancel.IsCancellationRequested)
        {
            var allReady = true;
            foreach (var ns in nameservers)
            {
                if (!await QueryDnsTxtContainsAsync(ns, fqdn, expectedTxt, cancel))
                {
                    allReady = false;
                    break;
                }
            }
            if (allReady)
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
        // Transaction ID (2 bytes), Flags: Standard query (0x0100), QDCOUNT=1, ANCOUNT=0, NSCOUNT=0, ARCOUNT=0
        ms.Write([0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        foreach (var label in fqdn.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0x00); // End of QNAME
        ms.Write([0x00, 0x10]); // QTYPE = TXT (16)
        ms.Write([0x00, 0x01]); // QCLASS = IN (1)
        return ms.ToArray();
    }

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "empty response";
        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonObject obj)
            {
                if (obj["detail"] is { } detail) return detail.ToString();
                var parts = obj.Select(kv => $"{kv.Key}: {kv.Value}").ToList();
                if (parts.Count > 0) return string.Join("; ", parts);
            }
            else if (node is JsonArray arr && arr.Count > 0)
            {
                return string.Join("; ", arr.Select(x => x?.ToString()));
            }
        }
        catch
        {
            // Fall through to raw string.
        }
        return body.Length > 200 ? body[..200] : body.Trim();
    }
}
