using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Penrill.Server.Hosting;

/// <summary>
/// Self-contained ACME v2 (RFC 8555) client for automatic Let's Encrypt certificate issuance and renewal
/// using the <c>dns-01</c> challenge (works on any port without needing port 80 or 443 open).
/// </summary>
public sealed class AcmeClient
{
    public const string ProductionDirectoryUrl = "https://acme-v02.api.letsencrypt.org/directory";
    private readonly HttpClient http;
    private readonly string directoryUrl;

    public AcmeClient(HttpClient? httpClient = null, string directoryUrl = ProductionDirectoryUrl)
    {
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PenrillServer-ACME/1.0");
        this.directoryUrl = directoryUrl;
    }

    /// <summary>
    /// Requests a publicly trusted TLS certificate for <paramref name="domain"/> via ACME v2 <c>dns-01</c>.
    /// </summary>
    public async Task<(bool Ok, string? FullChainPem, string? PrivateKeyPem, string? Error)> OrderDns01CertificateAsync(
        string tlsDirectory,
        string domain,
        string? contactEmail,
        Func<string, CancellationToken, Task<(bool Ok, string? Error)>> setDnsTxtCallback,
        Func<CancellationToken, Task> clearDnsTxtCallback,
        Action<string>? onProgress,
        CancellationToken cancel)
    {
        try
        {
            Directory.CreateDirectory(tlsDirectory);
            var accountKeyPath = Path.Combine(tlsDirectory, "acme-account.key");
            using var accountKey = LoadOrCreateAccountKey(accountKeyPath);
            var thumbprint = ComputeJwkThumbprint(accountKey);

            onProgress?.Invoke("Connecting to Let's Encrypt ACME v2 directory...");
            var dirJson = JsonNode.Parse(await http.GetStringAsync(directoryUrl, cancel))?.AsObject()
                ?? throw new InvalidOperationException("Could not parse ACME directory.");

            var newNonceUrl = (string?)dirJson["newNonce"] ?? throw new InvalidOperationException("Missing newNonce URL.");
            var newAccountUrl = (string?)dirJson["newAccount"] ?? throw new InvalidOperationException("Missing newAccount URL.");
            var newOrderUrl = (string?)dirJson["newOrder"] ?? throw new InvalidOperationException("Missing newOrder URL.");

            string? currentNonce = null;

            async Task<string> GetNonceAsync()
            {
                if (!string.IsNullOrEmpty(currentNonce))
                {
                    var n = currentNonce;
                    currentNonce = null;
                    return n;
                }
                using var req = new HttpRequestMessage(HttpMethod.Head, newNonceUrl);
                using var resp = await http.SendAsync(req, cancel);
                return resp.Headers.TryGetValues("Replay-Nonce", out var values)
                    ? values.First()
                    : throw new InvalidOperationException("ACME server did not provide a Replay-Nonce.");
            }

            async Task<(HttpResponseMessage Resp, string Body, string? Location)> PostJoseAsync(
                string url, object? payload, string? kid)
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var nonce = await GetNonceAsync();
                    var jwsBody = SignJws(accountKey, url, nonce, kid, payload);
                    var content = new ByteArrayContent(Encoding.UTF8.GetBytes(jwsBody));
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/jose+json");
                    using var req = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = content
                    };
                    var resp = await http.SendAsync(req, cancel);
                    if (resp.Headers.TryGetValues("Replay-Nonce", out var nonces))
                        currentNonce = nonces.FirstOrDefault();

                    var body = await resp.Content.ReadAsStringAsync(cancel);
                    if (!resp.IsSuccessStatusCode && body.Contains("badNonce", StringComparison.OrdinalIgnoreCase) && attempt < 2)
                    {
                        resp.Dispose();
                        continue;
                    }
                    var location = resp.Headers.Location?.ToString();
                    return (resp, body, location);
                }
                throw new InvalidOperationException("ACME request failed after retrying badNonce.");
            }

            // 1. Register or look up ACME account
            onProgress?.Invoke("Registering ACME account with Let's Encrypt...");
            object accountPayload = !string.IsNullOrWhiteSpace(contactEmail) && contactEmail.Contains('@') && !contactEmail.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
                ? new { termsOfServiceAgreed = true, contact = new[] { $"mailto:{contactEmail.Trim()}" } }
                : new { termsOfServiceAgreed = true };

            var (accResp, accBody, accLocation) = await PostJoseAsync(newAccountUrl, accountPayload, kid: null);
            using (accResp)
            {
                if (!accResp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(accLocation))
                    return (false, null, null, $"Let's Encrypt account registration failed: {ExtractAcmeError(accBody)}");
            }
            var kid = accLocation;

            // 2. Create certificate order
            onProgress?.Invoke($"Creating Let's Encrypt order for {domain}...");
            var orderPayload = new
            {
                identifiers = new[] { new { type = "dns", value = domain } }
            };
            string orderBody;
            string orderUrl;
            var (orderResp, ob, ol) = await PostJoseAsync(newOrderUrl, orderPayload, kid);
            using (orderResp)
            {
                orderBody = ob;
                orderUrl = ol ?? "";
                if (!orderResp.IsSuccessStatusCode)
                    return (false, null, null, $"Let's Encrypt order creation failed: {ExtractAcmeError(orderBody)}");
            }

            var orderJson = JsonNode.Parse(orderBody)?.AsObject()
                ?? throw new InvalidOperationException("Invalid ACME order response.");
            var authUrls = orderJson["authorizations"]?.AsArray().Select(x => (string?)x).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList() ?? [];
            var finalizeUrl = (string?)orderJson["finalize"] ?? throw new InvalidOperationException("ACME order missing finalize URL.");

            // 3. Complete DNS-01 authorization
            try
            {
                foreach (var authUrl in authUrls)
                {
                    var (authResp, authBody, _) = await PostJoseAsync(authUrl, payload: null, kid);
                    using (authResp)
                    {
                        if (!authResp.IsSuccessStatusCode)
                            return (false, null, null, $"Failed to fetch ACME authorization: {ExtractAcmeError(authBody)}");
                    }

                    var authJson = JsonNode.Parse(authBody)?.AsObject()
                        ?? throw new InvalidOperationException("Invalid ACME authorization JSON.");
                    var status = (string?)authJson["status"];
                    if (string.Equals(status, "valid", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var challenges = authJson["challenges"]?.AsArray() ?? [];
                    var dnsChallenge = challenges
                        .Select(c => c?.AsObject())
                        .FirstOrDefault(c => string.Equals((string?)c?["type"], "dns-01", StringComparison.OrdinalIgnoreCase));

                    if (dnsChallenge is null)
                        return (false, null, null, "Let's Encrypt did not offer a dns-01 challenge for this domain.");

                    var challengeToken = (string?)dnsChallenge["token"] ?? throw new InvalidOperationException("Missing dns-01 token.");
                    var challengeUrl = (string?)dnsChallenge["url"] ?? throw new InvalidOperationException("Missing dns-01 challenge URL.");
                    var txtDigest = ComputeDns01Digest(challengeToken, thumbprint);

                    onProgress?.Invoke($"Publishing _acme-challenge.{domain} TXT record via deSEC API...");
                    var (dnsOk, dnsError) = await setDnsTxtCallback(txtDigest, cancel);
                    if (!dnsOk)
                        return (false, null, null, dnsError ?? "Failed to publish _acme-challenge DNS TXT record.");

                    onProgress?.Invoke("Triggering Let's Encrypt DNS-01 verification...");
                    var (trigResp, trigBody, _) = await PostJoseAsync(challengeUrl, new { }, kid);
                    using (trigResp)
                    {
                        if (!trigResp.IsSuccessStatusCode)
                            return (false, null, null, $"Failed to trigger DNS-01 challenge: {ExtractAcmeError(trigBody)}");
                    }

                    // Poll authorization until valid or invalid
                    var authValid = false;
                    for (var poll = 0; poll < 30; poll++)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), cancel);
                        var (pollResp, pollBody, _) = await PostJoseAsync(authUrl, payload: null, kid);
                        using (pollResp)
                        {
                            var pollJson = JsonNode.Parse(pollBody)?.AsObject();
                            var pollStatus = (string?)pollJson?["status"];
                            if (string.Equals(pollStatus, "valid", StringComparison.OrdinalIgnoreCase))
                            {
                                authValid = true;
                                break;
                            }
                            if (string.Equals(pollStatus, "invalid", StringComparison.OrdinalIgnoreCase))
                            {
                                return (false, null, null, $"Let's Encrypt DNS-01 validation failed: {ExtractAcmeError(pollBody)}");
                            }
                        }
                    }

                    if (!authValid)
                        return (false, null, null, "Timed out waiting for Let's Encrypt DNS-01 validation.");
                }
            }
            finally
            {
                await clearDnsTxtCallback(cancel);
            }

            // 4. Generate domain key & CSR and finalize order
            onProgress?.Invoke("DNS-01 verified! Finalizing certificate order with Let's Encrypt...");
            using var domainKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var csrDer = CreateCsrDer(domainKey, domain);
            var (finResp, finBody, _) = await PostJoseAsync(finalizeUrl, new { csr = Base64UrlEncode(csrDer) }, kid);
            using (finResp)
            {
                if (!finResp.IsSuccessStatusCode)
                    return (false, null, null, $"Let's Encrypt order finalization failed: {ExtractAcmeError(finBody)}");
            }

            var finalJson = JsonNode.Parse(finBody)?.AsObject();
            var certUrl = (string?)finalJson?["certificate"];

            if (string.IsNullOrWhiteSpace(certUrl) && !string.IsNullOrWhiteSpace(orderUrl))
            {
                for (var poll = 0; poll < 20; poll++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancel);
                    var (ordPollResp, ordPollBody, _) = await PostJoseAsync(orderUrl, payload: null, kid);
                    using (ordPollResp)
                    {
                        var ordPollJson = JsonNode.Parse(ordPollBody)?.AsObject();
                        certUrl = (string?)ordPollJson?["certificate"];
                        var ordStatus = (string?)ordPollJson?["status"];
                        if (!string.IsNullOrWhiteSpace(certUrl))
                            break;
                        if (string.Equals(ordStatus, "invalid", StringComparison.OrdinalIgnoreCase))
                            return (false, null, null, $"Let's Encrypt order became invalid: {ExtractAcmeError(ordPollBody)}");
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(certUrl))
                return (false, null, null, "Let's Encrypt did not return a certificate download URL.");

            // 5. Download PEM certificate chain
            onProgress?.Invoke("Downloading signed SSL certificate chain...");
            var (certResp, certPem, _) = await PostJoseAsync(certUrl, payload: null, kid);
            using (certResp)
            {
                if (!certResp.IsSuccessStatusCode || !certPem.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
                    return (false, null, null, $"Failed to download certificate chain: {ExtractAcmeError(certPem)}");
            }

            return (true, certPem, domainKey.ExportPkcs8PrivateKeyPem(), null);
        }
        catch (Exception ex)
        {
            return (false, null, null, $"ACME certificate provisioning error: {ex.Message}");
        }
    }

    public static string ComputeJwkThumbprint(ECDsa key)
    {
        var p = key.ExportParameters(false);
        var x = Base64UrlEncode(p.Q.X!);
        var y = Base64UrlEncode(p.Q.Y!);
        // RFC 7638 canonical JSON: lexicographical key order, no whitespace.
        var canonical = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{x}\",\"y\":\"{y}\"}}";
        return Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string ComputeDns01Digest(string challengeToken, string jwkThumbprint)
    {
        var keyAuthorization = $"{challengeToken}.{jwkThumbprint}";
        return Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(keyAuthorization)));
    }

    private static string SignJws(ECDsa key, string url, string nonce, string? kid, object? payload)
    {
        string protectedJson;
        if (kid is null)
        {
            var p = key.ExportParameters(false);
            var jwk = new
            {
                crv = "P-256",
                kty = "EC",
                x = Base64UrlEncode(p.Q.X!),
                y = Base64UrlEncode(p.Q.Y!)
            };
            protectedJson = JsonSerializer.Serialize(new { alg = "ES256", jwk, nonce, url });
        }
        else
        {
            protectedJson = JsonSerializer.Serialize(new { alg = "ES256", kid, nonce, url });
        }

        var protected64 = Base64UrlEncode(Encoding.UTF8.GetBytes(protectedJson));
        var payload64 = payload is null
            ? ""
            : Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));

        var signingInput = Encoding.ASCII.GetBytes($"{protected64}.{payload64}");
        var signature = key.SignData(signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var signature64 = Base64UrlEncode(signature);

        return JsonSerializer.Serialize(new
        {
            @protected = protected64,
            payload = payload64,
            signature = signature64
        });
    }

    private static byte[] CreateCsrDer(ECDsa domainKey, string domain)
    {
        var req = new CertificateRequest($"CN={domain}", domainKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(domain);
        req.CertificateExtensions.Add(san.Build());
        return req.CreateSigningRequest();
    }

    private static ECDsa LoadOrCreateAccountKey(string path)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path))
        {
            key.ImportFromPem(File.ReadAllText(path));
        }
        else
        {
            PrivateFile.Write(path, key.ExportPkcs8PrivateKeyPem());
        }
        return key;
    }

    public static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string ExtractAcmeError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "empty response";
        try
        {
            var node = JsonNode.Parse(body)?.AsObject();
            if (node is not null)
            {
                var detail = (string?)node["detail"];
                var type = (string?)node["type"];
                if (!string.IsNullOrWhiteSpace(detail))
                    return !string.IsNullOrWhiteSpace(type) ? $"{detail} ({type})" : detail;

                if (node["challenges"] is JsonArray challenges)
                {
                    foreach (var ch in challenges)
                    {
                        if (ch?["error"] is JsonObject errObj && (string?)errObj["detail"] is { Length: > 0 } chDetail)
                            return chDetail;
                    }
                }
            }
        }
        catch
        {
            // Ignore JSON parse error.
        }
        return body.Length > 240 ? body[..240] : body.Trim();
    }
}
