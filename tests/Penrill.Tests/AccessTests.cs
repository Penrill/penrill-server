using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Penrill.Core.Accounts;
using Penrill.Core.Linking;
using Penrill.Server.Hosting;

namespace Penrill.Tests;

/// <summary>Claiming a server, invites, password resets, device linking and revoking devices.</summary>
public sealed class AccessTests : IDisposable
{
    private const string Password = "correct horse battery";
    private readonly string directory = Directory.CreateTempSubdirectory("penrill-access-").FullName;
    private readonly WebApplicationFactory<Program> factory;

    public AccessTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("PENRILL_DATA_DIR", directory));
    }

    public void Dispose()
    {
        factory.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    private GrantService Grants => factory.Services.GetRequiredService<GrantService>();

    private async Task<string> Owner(string name = "ann")
    {
        var claim = await Grants.CreateClaim(default);
        Assert.NotNull(claim);
        Assert.Equal(RedeemStatus.Ok, (await Grants.Claim(claim, name, Password, default)).Status);
        return name;
    }

    private async Task<(HttpClient Client, string Token)> SignIn(string name)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/login", new { name, password = Password });
        response.EnsureSuccessStatusCode();
        var token = (string)(await response.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, token);
    }

    private static HttpClient As(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task RegistrationIsClosedByDefault()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/register", new { name = "eve", password = Password });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var server = await factory.CreateClient().GetFromJsonAsync<JsonObject>("/api/server");
        Assert.Equal("invite", (string)server!["registration"]!);
    }

    [Fact]
    public async Task OnlyOneClaimMakesAnOwner()
    {
        var claim = await Grants.CreateClaim(default);
        Assert.Equal(RedeemStatus.Ok, (await Grants.Claim(claim, "ann", Password, default)).Status);
        Assert.Equal(RedeemStatus.Expired, (await Grants.Claim(claim, "bob", Password, default)).Status);
        Assert.Null(await Grants.CreateClaim(default));
        var owner = await factory.Services.GetRequiredService<AccountService>().Find("ann", default);
        Assert.Equal(Role.Owner, owner!.Role);
    }

    [Fact]
    public async Task InvitesAreSingleUseAndSurviveATakenName()
    {
        var owner = await Owner();
        var invite = await Grants.CreateInvite(owner, Role.Member, null, 1, default);
        Assert.Equal(RedeemStatus.Taken, (await Grants.AcceptInvite(invite.Token, owner, Password, default)).Status);
        Assert.Equal(RedeemStatus.Ok, (await Grants.AcceptInvite(invite.Token, "bob", Password, default)).Status);
        Assert.Equal(RedeemStatus.Expired, (await Grants.AcceptInvite(invite.Token, "cid", Password, default)).Status);
        await SignIn("bob");
    }

    [Fact]
    public async Task ResetLinkSetsPasswordAndSignsOutEverywhere()
    {
        var owner = await Owner();
        var (client, _) = await SignIn(owner);
        var reset = await Grants.CreateReset(owner, owner, default);
        Assert.Equal(RedeemStatus.Ok, (await Grants.Reset(reset!.Token, "another long password", default)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/boards")).StatusCode);
        var response = await factory.CreateClient().PostAsJsonAsync("/api/login", new { name = owner, password = "another long password" });
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task LinksADeviceAfterApproval()
    {
        var owner = await Owner();
        var editor = factory.CreateClient();
        var start = await (await editor.PostAsJsonAsync("/api/link/start", new { device = "MacBook", app = "penrill 0.14" })).Content.ReadFromJsonAsync<JsonObject>();
        var userCode = (string)start!["userCode"]!;
        var deviceCode = (string)start["deviceCode"]!;
        Assert.Matches("^[A-Z]{4}-[A-Z]{4}$", userCode);
        Assert.EndsWith("/link?code=" + userCode, (string)start["verifyUrlComplete"]!);

        var (browser, _) = await SignIn(owner);
        var info = await browser.GetFromJsonAsync<JsonObject>("/api/link/" + userCode.ToLowerInvariant().Replace("-", " "));
        Assert.Equal("MacBook", (string)info!["device"]!);

        var poll = editor.PostAsJsonAsync("/api/link/poll", new { deviceCode });
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsJsonAsync("/api/link/" + userCode, new { allow = true })).StatusCode);
        var linked = await (await poll).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(owner, (string)linked!["name"]!);
        var token = (string)linked["token"]!;

        Assert.Equal(HttpStatusCode.Gone, (await editor.PostAsJsonAsync("/api/link/poll", new { deviceCode })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(factory.CreateClient(), token).GetAsync("/api/boards")).StatusCode);

        var devices = await browser.GetFromJsonAsync<JsonArray>("/api/devices");
        var device = Assert.Single(devices!);
        Assert.Equal("MacBook", (string)device!["name"]!);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync($"/api/devices/{device["id"]}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(factory.CreateClient(), token).GetAsync("/api/boards")).StatusCode);
    }

    [Fact]
    public async Task DeniedLinkGivesNoToken()
    {
        var owner = await Owner();
        var links = factory.Services.GetRequiredService<LinkService>();
        var start = links.Start("Laptop", "", "")!;
        Assert.True(links.Decide(start.UserCode, owner, allow: false));
        Assert.False(links.Decide(start.UserCode, owner, allow: true));
        Assert.Equal(PollStatus.Denied, (await links.Poll(start.DeviceCode, default)).Status);
    }

    [Fact]
    public async Task DisablingAUserRevokesTheirDevices()
    {
        var owner = await Owner();
        var invite = await Grants.CreateInvite(owner, Role.Member, null, 1, default);
        await Grants.AcceptInvite(invite.Token, "bob", Password, default);
        var token = await factory.Services.GetRequiredService<AccountService>().IssueDeviceToken("bob", "Tablet", default);
        var admin = factory.Services.GetRequiredService<Penrill.Core.Admin.AdminService>();
        Assert.False(await admin.SetDisabled(owner, true, default));
        Assert.True(await admin.SetDisabled("bob", true, default));
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(factory.CreateClient(), token).GetAsync("/api/boards")).StatusCode);
    }

    [Fact]
    public async Task AdminCanCreateSetPasswordAndDeleteUsers()
    {
        var owner = await Owner();
        var admin = factory.Services.GetRequiredService<Penrill.Core.Admin.AdminService>();
        Assert.Equal(SignInStatus.Ok, (await admin.CreateUser("bob", Password, Role.Member, default)).Status);
        var (bobClient, _) = await SignIn("bob");

        Assert.True(await admin.SetPassword("bob", "brand new password", default));
        Assert.Equal(HttpStatusCode.Unauthorized, (await bobClient.GetAsync("/api/boards")).StatusCode);
        var relogin = await factory.CreateClient().PostAsJsonAsync("/api/login", new { name = "bob", password = "brand new password" });
        relogin.EnsureSuccessStatusCode();

        Assert.False(await admin.DeleteUser(owner, default));
        Assert.True(await admin.DeleteUser("bob", default));
        var afterDelete = await factory.CreateClient().PostAsJsonAsync("/api/login", new { name = "bob", password = "brand new password" });
        Assert.Equal(HttpStatusCode.Unauthorized, afterDelete.StatusCode);
    }

    [Fact]
    public void PublicDomainSniCertificateAndPinlessConnectLinks()
    {
        Assert.Equal(("myboard", "myboard.duckdns.org"), DuckDnsClient.NormalizeDomain("myboard"));
        Assert.Equal(("my-board", "my-board.duckdns.org"), DuckDnsClient.NormalizeDomain("https://My-Board.duckdns.org:5081/"));
        Assert.Null(DuckDnsClient.NormalizeDomain("other.example.com"));

        Assert.Equal("myboard.dedyn.io", DesecClient.NormalizeDomain("myboard"));
        Assert.Equal("myboard.dedyn.io", DesecClient.NormalizeDomain("https://MyBoard.dedyn.io:5081/"));
        Assert.Null(DesecClient.NormalizeDomain("192.168.1.253"));

        using var acmeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var thumbprint = AcmeClient.ComputeJwkThumbprint(acmeKey);
        var digest = AcmeClient.ComputeDns01Digest("token123", thumbprint);
        Assert.Equal(43, thumbprint.Length);
        Assert.Equal(43, digest.Length);

        var certDir = Directory.CreateTempSubdirectory("penrill-tls-").FullName;
        try
        {
            var serverCert = new ServerCertificate(certDir);
            var address = new ServerAddress(AddressMode.Lan, "", 5081, serverCert);

            Assert.NotNull(address.PinFor("https://192.168.1.253:5081"));
            Assert.NotNull(address.PinFor("https://myboard.dedyn.io:5081"));

            using var domainKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest("CN=myboard.dedyn.io", domainKey, HashAlgorithmName.SHA256);
            using var issued = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(90));
            serverCert.SetTrustedDomainCertificate(
                "myboard.dedyn.io",
                issued.ExportCertificatePem(),
                domainKey.ExportPkcs8PrivateKeyPem());

            Assert.Equal("myboard.dedyn.io", serverCert.TrustedDomain);
            Assert.Equal(serverCert.TrustedCertificate!.Thumbprint, serverCert.SelectCertificate("myboard.dedyn.io").Thumbprint);
            Assert.Equal(serverCert.Certificate.Thumbprint, serverCert.SelectCertificate("192.168.1.253").Thumbprint);

            // Public domain with trusted cert gets pin == null; LAN address still gets self-signed pin.
            Assert.Null(address.PinFor("https://myboard.dedyn.io:5081"));
            Assert.Equal(serverCert.Pin, address.PinFor("https://192.168.1.253:5081"));

            var links = new ConnectLinks(ConnectLinks.DefaultPage);
            var publicLink = links.For("https://myboard.dedyn.io:5081", address.PinFor("https://myboard.dedyn.io:5081"));
            var lanLink = links.For("https://192.168.1.253:5081", address.PinFor("https://192.168.1.253:5081"));
            Assert.DoesNotContain("&pin=", publicLink);
            Assert.Contains("&pin=", lanLink);
        }
        finally
        {
            Directory.Delete(certDir, recursive: true);
        }
    }

    [Fact]
    public void OneAddressCannotTakeEveryLinkSlot()
    {
        var links = factory.Services.GetRequiredService<LinkService>();
        var limit = factory.Services.GetRequiredService<LinkOptions>().MaxPendingPerAddress;
        for (var i = 0; i < limit; i++) Assert.NotNull(links.Start("Tablet", "", "203.0.113.7"));
        Assert.Null(links.Start("Tablet", "", "203.0.113.7"));
        Assert.NotNull(links.Start("Tablet", "", "203.0.113.8"));
    }

    [Fact]
    public async Task ExpiredLinkRequestsAreForgotten()
    {
        var time = new FakeTime();
        var links = new LinkService(factory.Services.GetRequiredService<AccountService>(), time, new LinkOptions { Lifetime = TimeSpan.FromMinutes(10) });
        var start = links.Start("Tablet", "", "203.0.113.7")!;
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(PollStatus.Expired, (await links.Poll(start.DeviceCode, default)).Status);
        Assert.Null(links.Describe(start.UserCode));
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }
}

