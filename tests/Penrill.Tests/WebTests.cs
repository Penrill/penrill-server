using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Penrill.Core.Accounts;

namespace Penrill.Tests;

/// <summary>The JSON API behind the server's own web app: setup, the session cookie, its guard against other sites, and roles.</summary>
public sealed class WebTests : IDisposable
{
    private const string Password = "correct horse battery";
    private readonly string directory = Directory.CreateTempSubdirectory("penrill-web-").FullName;
    private readonly WebApplicationFactory<Program> factory;

    public WebTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("PENRILL_DATA_DIR", directory));
    }

    public void Dispose()
    {
        factory.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
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
        var claim = await factory.Services.GetRequiredService<GrantService>().CreateClaim(default);
        var browser = Browser();
        var response = await browser.PostAsJsonAsync("/web/setup", new { token = claim, name = "ann", password = Password, serverName = "Lyceum 7", email = "ann@example.com" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return browser;
    }

    [Fact]
    public async Task SetupNeedsAnEmailAndSignsTheOwnerIn()
    {
        var claim = await factory.Services.GetRequiredService<GrantService>().CreateClaim(default);
        var browser = Browser();
        var missing = await browser.PostAsJsonAsync("/web/setup", new { token = claim, name = "ann", password = Password, serverName = "Lyceum 7", email = "" });
        Assert.Equal("email", (string)(await missing.Content.ReadFromJsonAsync<JsonObject>())!["error"]!);

        var done = await browser.PostAsJsonAsync("/web/setup", new { token = claim, name = "ann", password = Password, serverName = "Lyceum 7", email = "ann@example.com" });
        Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);
        var session = await browser.GetFromJsonAsync<JsonObject>("/web/session");
        Assert.Equal("Lyceum 7", (string)session!["server"]!);
        Assert.False((bool)session["unclaimed"]!);
        Assert.Equal("owner", (string)session["user"]!["role"]!);
        var server = await browser.GetFromJsonAsync<JsonObject>("/web/admin/server");
        Assert.Equal("ann@example.com", (string)server!["email"]!);
    }

    [Fact]
    public async Task ChangesNeedTheHeaderAndTheSameSite()
    {
        var browser = await Owner();
        browser.DefaultRequestHeaders.Remove("X-Penrill-Web");
        var bare = await browser.PostAsJsonAsync("/web/admin/invites", new { role = "Member", uses = 1, days = 7 });
        Assert.Equal(HttpStatusCode.Forbidden, bare.StatusCode);

        browser.DefaultRequestHeaders.Add("X-Penrill-Web", "1");
        var request = new HttpRequestMessage(HttpMethod.Post, "/web/admin/invites") { Content = JsonContent.Create(new { role = "Member", uses = 1, days = 7 }) };
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.SendAsync(request)).StatusCode);

        var invite = await browser.PostAsJsonAsync("/web/admin/invites", new { role = "Member", uses = 1, days = 7 });
        invite.EnsureSuccessStatusCode();
        Assert.Contains("/invite/", (string)(await invite.Content.ReadFromJsonAsync<JsonObject>())!["url"]!);
    }

    [Fact]
    public async Task MembersCannotManageAndAnonymousIsTurnedAway()
    {
        var owner = await Owner();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Browser().GetAsync("/web/home")).StatusCode);

        var invite = await (await owner.PostAsJsonAsync("/web/admin/invites", new { role = "Member", uses = 1, days = 7 })).Content.ReadFromJsonAsync<JsonObject>();
        var token = ((string)invite!["url"]!).Split("/invite/")[1];
        var member = Browser();
        Assert.Equal(HttpStatusCode.NoContent, (await member.GetAsync($"/web/invite/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await member.PostAsJsonAsync($"/web/invite/{token}", new { name = "bob", password = Password })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/web/home")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/web/admin/people")).StatusCode);
        var promote = await owner.PutAsJsonAsync("/web/admin/people/ann/role", new { role = "Member" });
        Assert.Equal("owner", (string)(await promote.Content.ReadFromJsonAsync<JsonObject>())!["error"]!);
    }

    [Fact]
    public async Task PagesFallBackToTheAppButApiPathsDoNot()
    {
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/nothing-here")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/web/nothing-here")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/assets/missing.js")).StatusCode);
    }
}
