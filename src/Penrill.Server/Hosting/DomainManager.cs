using System.Net;
using Microsoft.Extensions.Logging;
using Penrill.Core.Admin;

namespace Penrill.Server.Hosting;

public sealed record PublicDomainStatusDto(
    string? Domain,
    string? Provider,
    string? Origin,
    string? PublicIp,
    string LocalIp,
    int Port,
    bool HasTrustedCertificate,
    string? CertificateIssuer,
    DateTimeOffset? CertificateExpires);

public sealed record DomainSetupResultDto(
    bool Ok,
    string? Domain,
    string? Origin,
    string? PublicIp,
    string LocalIp,
    int Port,
    bool DomainCreated,
    bool CertificateIssued,
    bool Reachable,
    string Message,
    IReadOnlyList<string> Steps);

/// <summary>
/// Manages public domain provisioning (deSEC <c>*.dedyn.io</c> or custom domains),
/// automatic background DDNS IP updates, and automatic Let's Encrypt DNS-01 certificate renewals.
/// </summary>
public sealed class DomainManager(
    AdminService admin,
    ServerAddress address,
    DesecClient desec,
    AcmeClient acme,
    ILogger<DomainManager> logger) : BackgroundService
{
    private static readonly TimeSpan RenewBeforeExpiry = TimeSpan.FromDays(30);
    private static readonly TimeSpan DdnsCheckInterval = TimeSpan.FromMinutes(5);
    private string? lastPublicIp;
    private DateTimeOffset lastAcmeAttempt = DateTimeOffset.MinValue;

    public async Task<PublicDomainStatusDto> GetStatusAsync(CancellationToken cancel, bool detectPublicIp = false)
    {
        var savedDomain = await admin.DomainName(cancel);
        var provider = await admin.DomainProvider(cancel);
        var cert = address.Certificate;
        var activeDomain = !string.IsNullOrWhiteSpace(savedDomain) ? savedDomain : cert?.TrustedDomain;
        var publicIp = lastPublicIp ?? (detectPublicIp ? await desec.DetectPublicIpv4Async(cancel) : null);
        if (publicIp is not null) lastPublicIp = publicIp;
        var localIp = ServerAddress.PrimaryAddress().ToString();
        var origin = !string.IsNullOrWhiteSpace(activeDomain)
            ? (address.Port == 443 ? $"https://{activeDomain}" : $"https://{activeDomain}:{address.Port}")
            : null;
        return new PublicDomainStatusDto(
            string.IsNullOrWhiteSpace(activeDomain) ? null : activeDomain,
            string.IsNullOrWhiteSpace(provider) ? null : provider,
            origin,
            publicIp,
            localIp,
            address.Port,
            cert?.TrustedCertificate is not null && !string.IsNullOrWhiteSpace(activeDomain) && cert.IsTrustedHost(activeDomain),
            cert?.TrustedIssuer,
            cert?.TrustedExpires);
    }

    public async Task<DomainSetupResultDto> ConfigureDesecAsync(
        string? rawDomain,
        string? apiToken,
        string? desecEmail,
        string? desecPassword,
        bool forceRenewCert,
        CancellationToken cancel)
    {
        var steps = new List<string>();
        var localIp = ServerAddress.PrimaryAddress().ToString();
        var port = address.Port;
        var domain = DesecClient.NormalizeDomain(rawDomain, defaultToDedyn: true);

        if (address.Certificate is not { } serverCert)
            return Fail("HTTPS mode (PENRILL_ADDRESS_MODE=lan) is required for public domain TLS certificates.");

        if (domain is null)
            return Fail("Enter a valid domain name (e.g. 'myboard' for myboard.dedyn.io, or a full domain).");

        var token = (apiToken ?? "").Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            token = (await admin.DesecToken(cancel)) ?? "";
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            if (string.IsNullOrWhiteSpace(desecEmail) || string.IsNullOrWhiteSpace(desecPassword))
                return Fail("Provide either a deSEC API token or your deSEC account email and password.");

            steps.Add("Signing in to deSEC to create a dedicated API token...");
            var (createdToken, loginErr) = await desec.CreateTokenFromLoginAsync(
                desecEmail,
                desecPassword,
                $"penrill-server-{Environment.MachineName}",
                cancel);
            if (createdToken is null)
                return Fail(loginErr ?? "Could not create deSEC API token.");
            token = createdToken;
            steps.Add("Created dedicated deSEC API token (password was not stored).");
        }

        var publicIp = await desec.DetectPublicIpv4Async(cancel);
        if (string.IsNullOrWhiteSpace(publicIp))
            return Fail("Could not detect this server's public IPv4 address.");
        lastPublicIp = publicIp;
        steps.Add($"Detected public IPv4 address: {publicIp}");

        var (existsOk, domainCreated, domainErr) = await desec.EnsureDomainExistsAsync(token, domain, cancel);
        if (!existsOk)
            return Fail(domainErr ?? $"Could not verify or create domain '{domain}' on deSEC.");

        steps.Add(domainCreated
            ? $"Created new domain '{domain}' in your deSEC account."
            : $"Verified existing domain '{domain}' in your deSEC account.");

        var (ddnsOk, ddnsErr) = await desec.UpdateAddressRecordAsync(token, domain, publicIp, cancel);
        if (!ddnsOk)
            return Fail(ddnsErr ?? $"Could not update DNS A record for '{domain}'.");
        steps.Add($"Updated DNS A record: {domain} → {publicIp}");

        var certIssued = false;
        if (forceRenewCert || serverCert.NeedsTrustedRenewal(domain, RenewBeforeExpiry))
        {
            var contactEmail = await admin.ContactEmail(cancel);
            var (acmeOk, fullChainPem, privateKeyPem, acmeErr) = await acme.OrderDns01CertificateAsync(
                serverCert.TlsDirectory,
                domain,
                contactEmail,
                (txtDigest, ct) => desec.SetAcmeTxtRecordAsync(token, domain, txtDigest, ct),
                ct => desec.ClearAcmeTxtRecordAsync(token, domain, ct),
                msg => steps.Add(msg),
                cancel);

            if (!acmeOk || fullChainPem is null || privateKeyPem is null)
                return Fail(acmeErr ?? "Let's Encrypt certificate provisioning failed.");

            serverCert.SetTrustedDomainCertificate(domain, fullChainPem, privateKeyPem);
            certIssued = true;
            steps.Add($"Installed trusted Let's Encrypt certificate for {domain} (valid until {serverCert.TrustedExpires:yyyy-MM-dd}).");
        }
        else
        {
            steps.Add($"Using existing valid SSL certificate for {domain} (expires {serverCert.TrustedExpires:yyyy-MM-dd}).");
        }

        await admin.SetDomainConfig(domain, "desec", token, cancel);
        var origin = port == 443 ? $"https://{domain}" : $"https://{domain}:{port}";
        await PrependActiveNetworkAsync(origin, cancel);

        var reachable = await ProbeExternalReachabilityAsync(origin, cancel);
        steps.Add(reachable
            ? $"Verified external HTTPS connectivity to {origin}/health!"
            : $"Domain & SSL certificate are ready, but {origin} is not reachable from the internet yet (forward TCP port {port} → {localIp}:{port} on your router).");

        return new DomainSetupResultDto(
            true,
            domain,
            origin,
            publicIp,
            localIp,
            port,
            domainCreated,
            certIssued,
            reachable,
            $"Public domain {origin} configured with a trusted SSL certificate.",
            steps);

        DomainSetupResultDto Fail(string error) =>
            new(false, domain, null, lastPublicIp, localIp, port, false, false, false, error, steps);
    }

    public async Task<DomainSetupResultDto> ConfigureCustomCertificateAsync(
        string? rawDomain,
        string fullChainPathOrPem,
        string privateKeyPathOrPem,
        CancellationToken cancel)
    {
        var steps = new List<string>();
        var localIp = ServerAddress.PrimaryAddress().ToString();
        var port = address.Port;

        if (address.Certificate is not { } serverCert)
            return new(false, null, null, lastPublicIp, localIp, port, false, false, false,
                "HTTPS mode (PENRILL_ADDRESS_MODE=lan) is required for TLS certificates.", steps);

        var domain = DesecClient.NormalizeDomain(rawDomain, defaultToDedyn: false);
        if (domain is null)
            return new(false, null, null, lastPublicIp, localIp, port, false, false, false,
                "Enter a valid domain name (e.g. boards.example.com).", steps);

        try
        {
            var chainPem = fullChainPathOrPem.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal)
                ? fullChainPathOrPem
                : await File.ReadAllTextAsync(fullChainPathOrPem.Trim(), cancel);
            var keyPem = privateKeyPathOrPem.Contains("PRIVATE KEY", StringComparison.Ordinal)
                ? privateKeyPathOrPem
                : await File.ReadAllTextAsync(privateKeyPathOrPem.Trim(), cancel);

            serverCert.SetTrustedDomainCertificate(domain, chainPem, keyPem);
            steps.Add($"Loaded SSL certificate for {domain} (issuer: {serverCert.TrustedIssuer}, expires: {serverCert.TrustedExpires:yyyy-MM-dd}).");
        }
        catch (Exception ex)
        {
            return new(false, domain, null, lastPublicIp, localIp, port, false, false, false,
                $"Could not load certificate or private key: {ex.Message}", steps);
        }

        await admin.SetDomainConfig(domain, "custom", "", cancel);
        var origin = port == 443 ? $"https://{domain}" : $"https://{domain}:{port}";
        await PrependActiveNetworkAsync(origin, cancel);
        var publicIp = await desec.DetectPublicIpv4Async(cancel);
        var reachable = await ProbeExternalReachabilityAsync(origin, cancel);

        return new DomainSetupResultDto(
            true,
            domain,
            origin,
            publicIp,
            localIp,
            port,
            false,
            true,
            reachable,
            $"Custom domain {origin} configured with SSL certificate.",
            steps);
    }

    public async Task RemoveDomainAsync(CancellationToken cancel)
    {
        var oldDomain = await admin.DomainName(cancel) ?? address.Certificate?.TrustedDomain;
        address.Certificate?.ClearTrustedDomainCertificate();
        await admin.SetDomainConfig("", "", "", cancel);

        if (!string.IsNullOrWhiteSpace(oldDomain))
        {
            var saved = await admin.SelectedNetworks(cancel);
            if (!string.IsNullOrWhiteSpace(saved))
            {
                var remaining = saved
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(o => !o.Contains(oldDomain, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                await admin.SetSelectedNetworks(string.Join(',', remaining), cancel);
                address.RefreshCertificate(remaining);
            }
        }
    }

    private async Task PrependActiveNetworkAsync(string domainOrigin, CancellationToken cancel)
    {
        var saved = await admin.SelectedNetworks(cancel);
        var current = string.IsNullOrWhiteSpace(saved)
            ? address.DiscoverEndpoints().Where(d => d.DefaultEnabled).Select(d => d.Origin).ToList()
            : saved.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        current.RemoveAll(o => string.Equals(o, domainOrigin, StringComparison.OrdinalIgnoreCase));
        current.Insert(0, domainOrigin);
        await admin.SetSelectedNetworks(string.Join(',', current), cancel);
        address.RefreshCertificate(current);
    }

    public static async Task<bool> ProbeExternalReachabilityAsync(string origin, CancellationToken cancel)
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = true };
            using var probeClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            using var resp = await probeClient.GetAsync($"{origin.TrimEnd('/')}/health", cancel);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay slightly after startup before first background DDNS/certificate check.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunMaintenanceTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Background public domain maintenance tick failed.");
            }

            try
            {
                await Task.Delay(DdnsCheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunMaintenanceTickAsync(CancellationToken cancel)
    {
        var domain = await admin.DomainName(cancel);
        var provider = await admin.DomainProvider(cancel);
        var token = await admin.DesecToken(cancel);
        if (string.IsNullOrWhiteSpace(domain) || provider != "desec" || string.IsNullOrWhiteSpace(token))
            return;

        var currentIp = await desec.DetectPublicIpv4Async(cancel);
        if (!string.IsNullOrWhiteSpace(currentIp) && currentIp != lastPublicIp)
        {
            var (ok, err) = await desec.UpdateAddressRecordAsync(token, domain, currentIp, cancel);
            if (ok)
            {
                lastPublicIp = currentIp;
                logger.LogInformation("Updated deSEC DDNS A record for {Domain} to {PublicIp}.", domain, currentIp);
            }
            else
            {
                logger.LogWarning("Could not update deSEC DDNS for {Domain}: {Error}", domain, err);
            }
        }

        if (address.Certificate is { } serverCert &&
            serverCert.NeedsTrustedRenewal(domain, RenewBeforeExpiry) &&
            DateTimeOffset.UtcNow - lastAcmeAttempt > TimeSpan.FromHours(6))
        {
            lastAcmeAttempt = DateTimeOffset.UtcNow;
            logger.LogInformation("Renewing Let's Encrypt TLS certificate for {Domain} via deSEC DNS-01...", domain);
            var email = await admin.ContactEmail(cancel);
            var (acmeOk, chainPem, keyPem, acmeErr) = await acme.OrderDns01CertificateAsync(
                serverCert.TlsDirectory,
                domain,
                email,
                (txt, ct) => desec.SetAcmeTxtRecordAsync(token, domain, txt, ct),
                ct => desec.ClearAcmeTxtRecordAsync(token, domain, ct),
                null,
                cancel);
            if (acmeOk && chainPem is not null && keyPem is not null)
            {
                serverCert.SetTrustedDomainCertificate(domain, chainPem, keyPem);
                logger.LogInformation("Successfully renewed Let's Encrypt certificate for {Domain} (expires {Expires:yyyy-MM-dd}).",
                    domain, serverCert.TrustedExpires);
            }
            else
            {
                logger.LogWarning("Automatic Let's Encrypt renewal for {Domain} failed: {Error}", domain, acmeErr);
            }
        }
    }
}
