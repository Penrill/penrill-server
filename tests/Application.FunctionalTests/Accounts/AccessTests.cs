using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Application.Accounts;
using Application.Admin;
using Application.Linking;
using Domain.Enums;
using Web.Hosting;

namespace Application.FunctionalTests.Accounts;

/// <summary>Claiming a server, invites, password resets, device linking and revoking devices on a self-hosted server.</summary>
public class AccessTests
{
    private const string Password = "correct horse battery";
    private ServerFactory _factory = null!;

    [SetUp]
    public async Task SetUp()
    {
        await FunctionalTestSetup.DbResetter.ResetAsync();
        _factory = new ServerFactory();
    }

    [TearDown]
    public async Task TearDown() => await _factory.DisposeAsync();

    private async Task<T> With<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    private Task<T> Grants<T>(Func<GrantService, Task<T>> action) => With(sp => action(sp.GetRequiredService<GrantService>()));

    private Task<T> Admin<T>(Func<AdminService, Task<T>> action) => With(sp => action(sp.GetRequiredService<AdminService>()));

    private async Task<string> Owner(string name = "ann")
    {
        var claim = await Grants(g => g.CreateClaim(default));
        claim.ShouldNotBeNull();
        (await Grants(g => g.Claim(claim, name, Password, default))).Status.ShouldBe(RedeemStatus.Ok);
        return name;
    }

    private async Task<(HttpClient Client, string Token)> SignIn(string name)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/login", new { name, password = Password });
        response.EnsureSuccessStatusCode();
        var token = (string)(await response.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, token);
    }

    private HttpClient As(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Test]
    public async Task RegistrationIsClosedByDefault()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/register", new { name = "eve", password = Password });
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var server = await _factory.CreateClient().GetFromJsonAsync<JsonObject>("/api/server");
        ((string)server!["registration"]!).ShouldBe("invite");
    }

    [Test]
    public async Task InvitesWorkThroughTheApiWhenRegistrationIsClosed()
    {
        var ownerName = await Owner();
        var (owner, _) = await SignIn(ownerName);

        var createdResponse = await owner.PostAsJsonAsync("/api/invites", new { });
        createdResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var created = (await createdResponse.Content.ReadFromJsonAsync<JsonObject>())!;
        var token = (string)created["token"]!;
        ((string)created["url"]!).ShouldEndWith("/invite/" + token);
        ((string)created["role"]!).ShouldBe("member");
        ((int)created["usesLeft"]!).ShouldBe(1);

        var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync($"/api/invites/{token}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var joined = await anonymous.PostAsJsonAsync($"/api/invites/{token}", new { name = "bob", password = Password });
        joined.StatusCode.ShouldBe(HttpStatusCode.OK);
        var bobToken = (string)(await joined.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        (await As(bobToken).GetAsync("/api/boards")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Deleting an invite cannot touch a password reset grant.
        var reset = (await Grants(g => g.CreateReset(ownerName, "bob", default)))!;
        (await owner.DeleteAsync($"/api/invites/{reset.Grant.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Grants(g => g.Peek(reset.Token, default))).ShouldNotBeNull();
    }

    [Test]
    public async Task OnlyOneClaimMakesAnOwner()
    {
        var claim = await Grants(g => g.CreateClaim(default));
        (await Grants(g => g.Claim(claim, "ann", Password, default))).Status.ShouldBe(RedeemStatus.Ok);
        (await Grants(g => g.Claim(claim, "bob", Password, default))).Status.ShouldBe(RedeemStatus.Expired);
        (await Grants(g => g.CreateClaim(default))).ShouldBeNull();
        var owner = await Admin(a => a.Users(default));
        owner.ShouldHaveSingleItem().Role.ShouldBe(Role.Owner);
    }

    [Test]
    public async Task InvitesAreSingleUseAndSurviveATakenName()
    {
        var owner = await Owner();
        var invite = await Grants(g => g.CreateInvite(owner, Role.Member, null, 1, default));
        (await Grants(g => g.AcceptInvite(invite.Token, owner, Password, default))).Status.ShouldBe(RedeemStatus.Taken);
        (await Grants(g => g.AcceptInvite(invite.Token, "bob", Password, default))).Status.ShouldBe(RedeemStatus.Ok);
        (await Grants(g => g.AcceptInvite(invite.Token, "cid", Password, default))).Status.ShouldBe(RedeemStatus.Expired);
        await SignIn("bob");
    }

    [Test]
    public async Task ResetLinkSetsPasswordAndSignsOutEverywhere()
    {
        var owner = await Owner();
        var (client, _) = await SignIn(owner);
        var reset = await Grants(g => g.CreateReset(owner, owner, default));
        (await Grants(g => g.Reset(reset!.Token, "another long password", default))).Status.ShouldBe(RedeemStatus.Ok);
        (await client.GetAsync("/api/boards")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/login", new { name = owner, password = "another long password" });
        response.EnsureSuccessStatusCode();
    }

    [Test]
    public async Task DeniedLinkGivesNoToken()
    {
        var owner = await Owner();
        var links = _factory.Services.GetRequiredService<LinkService>();
        var start = links.Start("Laptop", "", "")!;
        links.Decide(start.UserCode, owner, allow: false).ShouldBeTrue();
        links.Decide(start.UserCode, owner, allow: true).ShouldBeFalse();
        (await links.Poll(start.DeviceCode, default)).Status.ShouldBe(PollStatus.Denied);
    }

    [Test]
    public async Task DisablingAUserRevokesTheirDevices()
    {
        var owner = await Owner();
        var invite = await Grants(g => g.CreateInvite(owner, Role.Member, null, 1, default));
        await Grants(g => g.AcceptInvite(invite.Token, "bob", Password, default));
        var token = await With(sp => sp.GetRequiredService<AccountService>().IssueDeviceToken("bob", "Tablet", default));
        (await Admin(a => a.SetDisabled(owner, true, default))).ShouldBeFalse();
        (await Admin(a => a.SetDisabled("bob", true, default))).ShouldBeTrue();
        (await As(token).GetAsync("/api/boards")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _factory.CreateClient().PostAsJsonAsync("/api/login", new { name = "bob", password = Password })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task AdminCanCreateSetPasswordAndDeleteUsers()
    {
        var owner = await Owner();
        (await Admin(a => a.CreateUser("bob", Password, Role.Member, default))).User.ShouldBe("bob");
        var (bobClient, _) = await SignIn("bob");

        (await Admin(a => a.SetPassword("bob", "brand new password", default))).ShouldBeTrue();
        (await bobClient.GetAsync("/api/boards")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var relogin = await _factory.CreateClient().PostAsJsonAsync("/api/login", new { name = "bob", password = "brand new password" });
        relogin.EnsureSuccessStatusCode();

        (await Admin(a => a.DeleteUser(owner, default))).ShouldBeFalse();
        (await Admin(a => a.DeleteUser("bob", default))).ShouldBeTrue();
        var afterDelete = await _factory.CreateClient().PostAsJsonAsync("/api/login", new { name = "bob", password = "brand new password" });
        afterDelete.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task BoardsOfADeletedAccountBecomeOrphansAnAdminCanHandOn()
    {
        var owner = await Owner();
        await Admin(a => a.CreateUser("bob", Password, Role.Member, default));
        var (bob, _) = await SignIn("bob");
        (await bob.PutAsync("/api/boards/00112233445566778899aabbccddeeff", JsonContent.Create(new { title = "B", background = "dots", items = Array.Empty<object>() })))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        (await Admin(a => a.DeleteUser("bob", default))).ShouldBeTrue();
        (await Admin(a => a.Orphans(default))).ShouldHaveSingleItem().Id.ShouldBe("00112233445566778899aabbccddeeff");
        (await Admin(a => a.AssignOwner("00112233445566778899aabbccddeeff", owner, default))).ShouldBeTrue();
        (await Admin(a => a.Orphans(default))).ShouldBeEmpty();
        var (ann, _) = await SignIn(owner);
        (await ann.GetAsync("/api/boards/00112233445566778899aabbccddeeff")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task BacksUpTheDatabaseAndImages()
    {
        await Owner();
        using var zip = new MemoryStream();
        await Admin(async a => { await a.Backup(zip, default); return 0; });
        zip.Position = 0;
        using var archive = new System.IO.Compression.ZipArchive(zip);
        archive.Entries.Select(e => e.FullName).ShouldContain(name => name is "penrill.dump" or "penrill.json");
    }

    [Test]
    public void PublicDomainSniCertificateAndPinlessConnectLinks()
    {
        DuckDnsClient.NormalizeDomain("myboard").ShouldBe(("myboard", "myboard.duckdns.org"));
        DuckDnsClient.NormalizeDomain("https://My-Board.duckdns.org:5081/").ShouldBe(("my-board", "my-board.duckdns.org"));
        DuckDnsClient.NormalizeDomain("other.example.com").ShouldBeNull();

        DesecClient.NormalizeDomain("myboard").ShouldBe("myboard.dedyn.io");
        DesecClient.NormalizeDomain("https://MyBoard.dedyn.io:5081/").ShouldBe("myboard.dedyn.io");
        DesecClient.NormalizeDomain("192.168.1.253").ShouldBeNull();

        using var acmeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var thumbprint = AcmeClient.ComputeJwkThumbprint(acmeKey);
        var digest = AcmeClient.ComputeDns01Digest("token123", thumbprint);
        thumbprint.Length.ShouldBe(43);
        digest.Length.ShouldBe(43);

        var certDir = Directory.CreateTempSubdirectory("penrill-tls-").FullName;
        try
        {
            var serverCert = new ServerCertificate(certDir);
            var address = new ServerAddress(AddressMode.Lan, "", 5081, serverCert);

            address.PinFor("https://192.168.1.253:5081").ShouldNotBeNull();
            address.PinFor("https://myboard.dedyn.io:5081").ShouldNotBeNull();

            using var domainKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest("CN=myboard.dedyn.io", domainKey, HashAlgorithmName.SHA256);
            using var issued = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(90));
            serverCert.SetTrustedDomainCertificate("myboard.dedyn.io", issued.ExportCertificatePem(), domainKey.ExportPkcs8PrivateKeyPem());

            serverCert.TrustedDomain.ShouldBe("myboard.dedyn.io");
            serverCert.SelectCertificate("myboard.dedyn.io").Thumbprint.ShouldBe(serverCert.TrustedCertificate!.Thumbprint);
            serverCert.SelectCertificate("192.168.1.253").Thumbprint.ShouldBe(serverCert.Certificate.Thumbprint);

            // Public domain with trusted cert gets pin == null; LAN address still gets self-signed pin.
            address.PinFor("https://myboard.dedyn.io:5081").ShouldBeNull();
            address.PinFor("https://192.168.1.253:5081").ShouldBe(serverCert.Pin);

            var links = new ConnectLinks(ConnectLinks.DefaultPage);
            links.For("https://myboard.dedyn.io:5081", address.PinFor("https://myboard.dedyn.io:5081")).ShouldNotContain("&pin=");
            links.For("https://192.168.1.253:5081", address.PinFor("https://192.168.1.253:5081")).ShouldContain("&pin=");
        }
        finally
        {
            Directory.Delete(certDir, recursive: true);
        }
    }

    [Test]
    public void OneAddressCannotTakeEveryLinkSlot()
    {
        var links = _factory.Services.GetRequiredService<LinkService>();
        var limit = _factory.Services.GetRequiredService<LinkOptions>().MaxPendingPerAddress;
        for (var i = 0; i < limit; i++) links.Start("Tablet", "", "203.0.113.7").ShouldNotBeNull();
        links.Start("Tablet", "", "203.0.113.7").ShouldBeNull();
        links.Start("Tablet", "", "203.0.113.8").ShouldNotBeNull();
    }

    [Test]
    public async Task ExpiredLinkRequestsAreForgotten()
    {
        var time = new FakeTime();
        var links = new LinkService(_factory.Services.GetRequiredService<IServiceScopeFactory>(), time, new LinkOptions { Lifetime = TimeSpan.FromMinutes(10) });
        var start = links.Start("Tablet", "", "203.0.113.7")!;
        time.Advance(TimeSpan.FromMinutes(11));
        (await links.Poll(start.DeviceCode, default)).Status.ShouldBe(PollStatus.Expired);
        links.Describe(start.UserCode).ShouldBeNull();
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
