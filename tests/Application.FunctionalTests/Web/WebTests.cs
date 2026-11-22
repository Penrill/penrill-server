using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Penrill.Application.Accounts;

namespace Penrill.Application.FunctionalTests.Web;

/// <summary>The JSON API behind the server's own web app: setup, the session cookie, its guard against other sites, and roles.</summary>
public class WebTests
{
    private const string Password = "correct horse battery";
    private ServerFactory factory = null!;

    [SetUp]
    public void SetUp() => factory = new ServerFactory();

    [TearDown]
    public async Task TearDown() => await factory.DisposeAsync();

    private async Task<string?> CreateClaim()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<GrantService>().CreateClaim(default);
    }

    /// <summary>A browser: keeps the cookie and sends the header the web app sends.</summary>
    private HttpClient Browser()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-Penrill-Web", "1");
        return client;
    }

    private async Task<HttpClient> Owner()
    {
        var claim = await CreateClaim();
        var browser = Browser();
        var response = await browser.PostAsJsonAsync("/web/setup", new { token = claim, name = "ann", password = Password, serverName = "Lyceum 7", email = "ann@example.com" });
        (response.StatusCode).ShouldBe(HttpStatusCode.NoContent);
        return browser;
    }

    [Test]
    public async Task SetupNeedsAnEmailAndSignsTheOwnerIn()
    {
        var claim = await CreateClaim();
        var browser = Browser();
        var missing = await browser.PostAsJsonAsync("/web/setup", new { token = claim, name = "ann", password = Password, serverName = "Lyceum 7", email = "" });
        ((string)(await missing.Content.ReadFromJsonAsync<JsonObject>())!["error"]!).ShouldBe("email");

        var done = await browser.PostAsJsonAsync("/web/setup", new { token = claim, name = "ann", password = Password, serverName = "Lyceum 7", email = "ann@example.com" });
        (done.StatusCode).ShouldBe(HttpStatusCode.NoContent);
        var session = await browser.GetFromJsonAsync<JsonObject>("/web/session");
        ((string)session!["server"]!).ShouldBe("Lyceum 7");
        ((bool)session["unclaimed"]!).ShouldBeFalse();
        ((string)session["user"]!["role"]!).ShouldBe("owner");
        var server = await browser.GetFromJsonAsync<JsonObject>("/web/admin/server");
        ((string)server!["email"]!).ShouldBe("ann@example.com");
    }

    [Test]
    public async Task ChangesNeedTheHeaderAndTheSameSite()
    {
        var browser = await Owner();
        browser.DefaultRequestHeaders.Remove("X-Penrill-Web");
        var bare = await browser.PostAsJsonAsync("/web/admin/invites", new { role = "Member", uses = 1, days = 7 });
        (bare.StatusCode).ShouldBe(HttpStatusCode.Forbidden);

        browser.DefaultRequestHeaders.Add("X-Penrill-Web", "1");
        var request = new HttpRequestMessage(HttpMethod.Post, "/web/admin/invites") { Content = JsonContent.Create(new { role = "Member", uses = 1, days = 7 }) };
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        ((await browser.SendAsync(request)).StatusCode).ShouldBe(HttpStatusCode.Forbidden);

        var invite = await browser.PostAsJsonAsync("/web/admin/invites", new { role = "Member", uses = 1, days = 7 });
        invite.EnsureSuccessStatusCode();
        ((string)(await invite.Content.ReadFromJsonAsync<JsonObject>())!["url"]!).ShouldContain("/invite/");
    }

    [Test]
    public async Task MembersCannotManageAndAnonymousIsTurnedAway()
    {
        var owner = await Owner();
        ((await Browser().GetAsync("/web/home")).StatusCode).ShouldBe(HttpStatusCode.Unauthorized);

        var invite = await (await owner.PostAsJsonAsync("/web/admin/invites", new { role = "Member", uses = 1, days = 7 })).Content.ReadFromJsonAsync<JsonObject>();
        var token = ((string)invite!["url"]!).Split("/invite/")[1];
        var member = Browser();
        ((await member.GetAsync($"/web/invite/{token}")).StatusCode).ShouldBe(HttpStatusCode.NoContent);
        ((await member.PostAsJsonAsync($"/web/invite/{token}", new { name = "bob", password = Password })).StatusCode).ShouldBe(HttpStatusCode.NoContent);

        ((await member.GetAsync("/web/home")).StatusCode).ShouldBe(HttpStatusCode.OK);
        ((await member.GetAsync("/web/admin/people")).StatusCode).ShouldBe(HttpStatusCode.Forbidden);
        var promote = await owner.PutAsJsonAsync("/web/admin/people/ann/role", new { role = "Member" });
        ((string)(await promote.Content.ReadFromJsonAsync<JsonObject>())!["error"]!).ShouldBe("owner");
    }

    [Test]
    public async Task PagesFallBackToTheAppButApiPathsDoNot()
    {
        var client = factory.CreateClient();
        ((await client.GetAsync("/api/nothing-here")).StatusCode).ShouldBe(HttpStatusCode.NotFound);
        ((await client.GetAsync("/web/nothing-here")).StatusCode).ShouldBe(HttpStatusCode.NotFound);
        ((await client.GetAsync("/assets/missing.js")).StatusCode).ShouldBe(HttpStatusCode.NotFound);
    }
}
