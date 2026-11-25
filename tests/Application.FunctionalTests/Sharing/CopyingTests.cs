using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Application.FunctionalTests.Sharing;

/// <summary>The owner decides whether people may copy a shared board, and whether they keep it once it is deleted.</summary>
public class CopyingTests
{
    private const string Board = "00112233445566778899aabbccddeeff";
    private ServerFactory factory = null!;

    [SetUp]
    public async Task SetUp()
    {
        await FunctionalTestSetup.DbResetter.ResetAsync();
        factory = new ServerFactory(openRegistration: true);
    }

    [TearDown]
    public async Task TearDown() => await factory.DisposeAsync();

    private async Task<HttpClient> SignUp(string name)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/register", new { name, password = "correct horse battery" });
        response.EnsureSuccessStatusCode();
        var token = (string)(await response.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<(HttpClient Ann, HttpClient Bob)> SharedBoard()
    {
        var ann = await SignUp("ann");
        var bob = await SignUp("bob");
        (await ann.PutAsync($"/api/boards/{Board}", JsonContent.Create(new { title = "Physics", background = "grid", items = Array.Empty<object>() })))
            .EnsureSuccessStatusCode();
        (await ann.PostAsJsonAsync($"/api/boards/{Board}/people", new { name = "bob" })).EnsureSuccessStatusCode();
        return (ann, bob);
    }

    private static async Task<JsonObject> Row(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonArray>("/api/boards"))!.Single(row => (string)row!["id"]! == Board)!.AsObject();

    [Test]
    public async Task OnlyTheOwnerDecidesWhetherOthersMayCopy()
    {
        var (ann, bob) = await SharedBoard();
        ((bool)(await Row(ann))["copying"]!).ShouldBeFalse();
        ((bool)(await Row(ann))["shared"]!).ShouldBeTrue();
        ((bool)(await Row(bob))["copying"]!).ShouldBeFalse();
        ((bool)(await Row(bob))["shared"]!).ShouldBeFalse();

        (await bob.PutAsJsonAsync($"/api/boards/{Board}/copying", new { allowed = true })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ann.PutAsJsonAsync($"/api/boards/{Board}/copying", new { allowed = true })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        ((bool)(await Row(bob))["copying"]!).ShouldBeTrue();
        ((bool)(await Row(ann))["copying"]!).ShouldBeTrue();
        (await ann.PutAsJsonAsync($"/api/boards/{Board}/copying", new { })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task ABoardNobodyElseCanOpenIsNotShared()
    {
        var ann = await SignUp("ann");
        (await ann.PutAsync($"/api/boards/{Board}", JsonContent.Create(new { title = "Mine", background = "grid", items = Array.Empty<object>() })))
            .EnsureSuccessStatusCode();
        ((bool)(await Row(ann))["shared"]!).ShouldBeFalse();
    }

    [TestCase(null)]
    [TestCase("keep")]
    public async Task ADeletedBoardIsJustMissingWhenCopiesStay(string? copies)
    {
        var (ann, bob) = await SharedBoard();
        var query = copies is null ? "" : $"?copies={copies}";
        (await ann.DeleteAsync($"/api/boards/{Board}{query}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var response = await bob.GetAsync($"/api/boards/{Board}");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<JsonObject>())!.ContainsKey("reason").ShouldBeFalse();
    }

    [Test]
    public async Task ADeletedBoardSaysItWasRemovedWhenCopiesGo()
    {
        var (ann, bob) = await SharedBoard();
        (await ann.DeleteAsync($"/api/boards/{Board}?copies=wipe")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ann.DeleteAsync($"/api/boards/{Board}?copies=remove")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var response = await bob.GetAsync($"/api/boards/{Board}");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        ((string)(await response.Content.ReadFromJsonAsync<JsonObject>())!["reason"]!).ShouldBe("removed");

        (await ann.PutAsync($"/api/boards/{Board}", JsonContent.Create(new { title = "Again", background = "grid", items = Array.Empty<object>() })))
            .EnsureSuccessStatusCode();
        (await bob.GetAsync($"/api/boards/{Board}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
