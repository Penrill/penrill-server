using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Penrill.Core.Boards;

namespace Penrill.Tests;

public sealed class ApiTests : IDisposable
{
    private const string Board = "00112233445566778899aabbccddeeff";
    private readonly string directory = Directory.CreateTempSubdirectory("penrill-api-").FullName;
    private readonly WebApplicationFactory<Program> factory;

    public ApiTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("PENRILL_DATA_DIR", directory).UseSetting("PENRILL_OPEN_REGISTRATION", "true"));
    }

    public void Dispose()
    {
        factory.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    private async Task<(HttpClient Client, string Token)> SignUp(string name)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/register", new { name, password = "correct horse battery" });
        response.EnsureSuccessStatusCode();
        var token = (string)(await response.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, token);
    }

    private static Task<HttpResponseMessage> CreateBoard(HttpClient client) =>
        client.PutAsync($"/api/boards/{Board}", JsonContent.Create(new { title = "Physics", background = "grid", items = new[] { new { id = "seed", z = 1 } } }));

    private static string Put(string changeId, string itemId) =>
        $$$"""{"id":"{{{changeId}}}","ops":[{"op":"put","item":{"id":"{{{itemId}}}","z":2}}]}""";

    private async Task<WebSocket> Connect(string token, long? after)
    {
        var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(new Uri($"ws://localhost/api/boards/{Board}/live"), default);
        var hello = new JsonObject { ["type"] = "hello", ["protocol"] = 1, ["token"] = token, ["after"] = after };
        await Send(socket, hello.ToJsonString());
        return socket;
    }

    private static Task Send(WebSocket socket, string json) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, default);

    private static async Task<JsonObject?> Next(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var message = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return JsonNode.Parse(message.ToArray())!.AsObject();
        }
    }

    private static async Task<JsonObject> NextOf(WebSocket socket, string type)
    {
        while (await Next(socket) is { } frame)
            if ((string)frame["type"]! == type) return frame;
        throw new InvalidOperationException($"The socket closed before a {type} frame.");
    }

    [Fact]
    public async Task RequiresASession()
    {
        var response = await factory.CreateClient().GetAsync("/api/boards");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OnlyTheOwnerDeletesABoardAndEveryoneOnItIsDisconnected()
    {
        var (ann, _) = await SignUp("ann");
        var (bob, bobToken) = await SignUp("bob");
        Assert.Equal(HttpStatusCode.Created, (await CreateBoard(ann)).StatusCode);
        (await ann.PostAsJsonAsync("/api/folders", new { name = "7-B" })).EnsureSuccessStatusCode();
        var folder = (string)(await ann.GetFromJsonAsync<JsonObject>("/api/community"))!["folders"]![0]!["id"]!;
        (await ann.PostAsJsonAsync("/api/members", new { folder, name = "bob" })).EnsureSuccessStatusCode();
        (await ann.PostAsJsonAsync("/api/share-board", new { folder, board = Board })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await bob.DeleteAsync($"/api/boards/{Board}")).StatusCode);
        var socket = await Connect(bobToken, null);
        await NextOf(socket, "snapshot");

        Assert.Equal(HttpStatusCode.NoContent, (await ann.DeleteAsync($"/api/boards/{Board}")).StatusCode);
        while (await Next(socket) is not null) { }
        Assert.Equal((WebSocketCloseStatus)4404, socket.CloseStatus);
        Assert.Equal(HttpStatusCode.NotFound, (await ann.GetAsync($"/api/boards/{Board}")).StatusCode);
        Assert.Empty((await bob.GetFromJsonAsync<JsonArray>("/api/boards"))!);
        Assert.Empty((await bob.GetFromJsonAsync<JsonObject>("/api/community"))!["boards"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await ann.DeleteAsync($"/api/boards/{Board}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateBoard(ann)).StatusCode);
    }

    [Fact]
    public async Task OwnersLetPeopleInByNameAndTakeThemOutAgain()
    {
        var (ann, _) = await SignUp("ann");
        var (bob, _) = await SignUp("bob");
        await CreateBoard(ann);
        var people = $"/api/boards/{Board}/people";

        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync($"/api/boards/{Board}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ann.PostAsJsonAsync(people, new { name = "nobody" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync(people, new { name = "bob" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ann.PostAsJsonAsync(people, new { name = "Bob" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ann.PostAsJsonAsync(people, new { name = "bob" })).StatusCode);
        Assert.Equal(["bob"], (await ann.GetFromJsonAsync<string[]>(people))!);

        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync($"/api/boards/{Board}")).StatusCode);
        Assert.Single((await bob.GetFromJsonAsync<JsonArray>("/api/boards"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync(people)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"{people}/bob")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await ann.DeleteAsync($"{people}/bob")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync($"/api/boards/{Board}")).StatusCode);
        Assert.Empty((await ann.GetFromJsonAsync<string[]>(people))!);
    }

    [Fact]
    public async Task AnyoneWithTheLinkDrawsWithoutAnAccountUntilItIsTurnedOff()
    {
        var (ann, _) = await SignUp("ann");
        await CreateBoard(ann);
        var link = $"/api/boards/{Board}/link";
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsJsonAsync("/api/join", new { token = "nope", name = "Kid" })).StatusCode);
        var (bob, _) = await SignUp("bob");
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync(link)).StatusCode);
        Assert.Null((await ann.GetFromJsonAsync<JsonObject>(link))!["token"]);
        var token = (string)(await (await ann.PostAsync(link, null)).Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        Assert.Equal(token, (string)(await (await ann.PostAsync(link, null)).Content.ReadFromJsonAsync<JsonObject>())!["token"]!);
        Assert.Equal(token, (string)(await ann.GetFromJsonAsync<JsonObject>(link))!["token"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/join", new { token })).StatusCode);

        var joined = (await (await anonymous.PostAsJsonAsync("/api/join", new { token, name = "Kid Ó" })).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.StartsWith("kid-", (string)joined["name"]!);
        Assert.Equal(Board, (string)joined["board"]!);
        var kid = factory.CreateClient();
        kid.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (string)joined["token"]!);
        Assert.Equal(HttpStatusCode.OK, (await kid.GetAsync($"/api/boards/{Board}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await kid.GetAsync(link)).StatusCode);
        // Opening the link again on the same device keeps the account it already has.
        var again = (await (await kid.PostAsJsonAsync("/api/join", new { token, name = "Kid" })).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal((string)joined["name"]!, (string)again["name"]!);
        Assert.Null(again["token"]);
        // So does bob, who has a real account: he just gets the board.
        Assert.Equal("bob", (string)(await (await bob.PostAsJsonAsync("/api/join", new { token })).Content.ReadFromJsonAsync<JsonObject>())!["name"]!);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync($"/api/boards/{Board}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await ann.DeleteAsync(link)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.GetAsync($"/api/boards/{Board}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync($"/api/boards/{Board}")).StatusCode);
        Assert.Null((await ann.GetFromJsonAsync<JsonObject>(link))!["token"]);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsJsonAsync("/api/join", new { token, name = "Kid" })).StatusCode);
    }

    [Fact]
    public async Task CreatingABoardTwiceIsSafe()
    {
        var (ann, _) = await SignUp("ann");
        Assert.Equal(HttpStatusCode.Created, (await CreateBoard(ann)).StatusCode);
        var again = await CreateBoard(ann);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.False((bool)(await again.Content.ReadFromJsonAsync<JsonObject>())!["created"]!);
        var (bob, _) = await SignUp("bob");
        Assert.Equal(HttpStatusCode.Conflict, (await CreateBoard(bob)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync($"/api/boards/{Board}")).StatusCode);
    }

    [Fact]
    public async Task SubmitsOverHttpAndCatchesUp()
    {
        var (ann, _) = await SignUp("ann");
        await CreateBoard(ann);
        var first = await ann.PostAsync($"/api/boards/{Board}/changes", new StringContent(Put("c1", "a"), Encoding.UTF8, "application/json"));
        Assert.Equal(1, (long)(await first.Content.ReadFromJsonAsync<JsonObject>())!["seq"]!);
        var repeated = await ann.PostAsync($"/api/boards/{Board}/changes", new StringContent(Put("c1", "a"), Encoding.UTF8, "application/json"));
        Assert.Equal(1, (long)(await repeated.Content.ReadFromJsonAsync<JsonObject>())!["seq"]!);
        var malformed = await ann.PostAsync($"/api/boards/{Board}/changes", new StringContent("""{"id":"c2","ops":[]}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        var changes = await ann.GetFromJsonAsync<JsonObject>($"/api/boards/{Board}/changes?after=0");
        Assert.Equal(1, (long)changes!["seq"]!);
        Assert.Single(changes["changes"]!.AsArray());
        Assert.Equal(HttpStatusCode.Gone, (await ann.GetAsync($"/api/boards/{Board}/changes?after=7")).StatusCode);

        var snapshot = await ann.GetFromJsonAsync<JsonObject>($"/api/boards/{Board}");
        Assert.Equal(2, snapshot!["items"]!.AsArray().Count);
        Assert.Equal("Physics", (string)snapshot["title"]!);
    }

    [Fact]
    public async Task LiveClientsShareChangesAndResumeAfterADrop()
    {
        var (ann, token) = await SignUp("ann");
        await CreateBoard(ann);

        var tablet = await Connect(token, after: 0);
        Assert.Equal("welcome", (string)(await Next(tablet))!["type"]!);
        Assert.Equal(0, (long)(await NextOf(tablet, "ready"))["seq"]!);
        var laptop = await Connect(token, after: null);
        Assert.Single((await NextOf(laptop, "snapshot"))["board"]!["items"]!.AsArray());
        await NextOf(laptop, "ready");

        await Send(tablet, $$"""{"type":"submit","change":{{Put("c1", "a")}}}""");
        Assert.Equal(1, (long)(await NextOf(tablet, "change"))["seq"]!);
        Assert.Equal("c1", (string)(await NextOf(laptop, "change"))["change"]!["id"]!);

        await tablet.CloseAsync(WebSocketCloseStatus.NormalClosure, "", default);
        await Send(laptop, $$"""{"type":"submit","change":{{Put("c2", "b")}}}""");
        await NextOf(laptop, "change");

        var resumed = await Connect(token, after: 1);
        var missed = await NextOf(resumed, "changes");
        Assert.Equal("c2", (string)missed["changes"]![0]!["change"]!["id"]!);
        await NextOf(resumed, "ready");

        await Send(resumed, $$"""{"type":"submit","change":{{Put("c1", "a")}}}""");
        var ack = await NextOf(resumed, "ack");
        Assert.Equal(1, (long)ack["seq"]!);
        await Send(resumed, """{"type":"submit","change":{"id":"c3","ops":[{"op":"teleport"}]}}""");
        Assert.Equal("c3", (string)(await NextOf(resumed, "reject"))["id"]!);
        await Send(resumed, """{"type":"ping"}""");
        await NextOf(resumed, "pong");
    }

    [Fact]
    public async Task LiveConnectionsNeedAValidSessionAndAccess()
    {
        var (ann, _) = await SignUp("ann");
        await CreateBoard(ann);
        var stranger = await Connect(new string('0', 64), after: null);
        Assert.Null(await Next(stranger));
        Assert.Equal((WebSocketCloseStatus)4401, stranger.CloseStatus);

        var (_, bobToken) = await SignUp("bob");
        var bob = await Connect(bobToken, after: null);
        Assert.Null(await Next(bob));
        Assert.Equal((WebSocketCloseStatus)4403, bob.CloseStatus);
    }

    [Fact]
    public async Task StoresImagesByTheirHash()
    {
        var (ann, _) = await SignUp("ann");
        await CreateBoard(ann);
        var png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 1, 2, 3 };
        var hash = Convert.ToHexStringLower(SHA256.HashData(png));
        ByteArrayContent Image(byte[] bytes)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return content;
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PutAsync($"/api/boards/{Board}/assets/{hash}", Image([1, 2]))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ann.PutAsync($"/api/boards/{Board}/assets/{hash}", Image(png))).StatusCode);
        var download = await ann.GetAsync($"/api/boards/{Board}/assets/{hash}");
        Assert.Equal(png, await download.Content.ReadAsByteArrayAsync());
        Assert.True(download.Headers.CacheControl!.Private);
    }

    [Fact]
    public async Task ImportsBoardsFromTheFirstServerVersion()
    {
        var image = new byte[] { 0x89, 0x50, 0x4e, 0x47, 9, 9 };
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(image);
        var file = new JsonObject
        {
            ["format"] = "inkboard", ["version"] = 10, ["background"] = "grid",
            ["assets"] = new JsonObject { ["img1"] = dataUrl },
            ["items"] = new JsonArray(new JsonObject { ["type"] = "image", ["id"] = "p", ["z"] = 1, ["asset"] = "img1" })
        };
        const string token = "1111111111111111111111111111111111111111111111111111111111111111";
        using (var db = new SqliteConnection($"Data Source={Path.Combine(directory, "boards.db")};Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE boards(id TEXT PRIMARY KEY, title TEXT NOT NULL, updated TEXT NOT NULL, revision INTEGER NOT NULL, board BLOB NOT NULL);
                CREATE TABLE owners(board TEXT PRIMARY KEY, user TEXT NOT NULL);
                CREATE TABLE sessions(token TEXT PRIMARY KEY, user TEXT NOT NULL, expires INTEGER NOT NULL);
                CREATE TABLE accounts(name TEXT PRIMARY KEY, password TEXT NOT NULL);
                INSERT INTO accounts VALUES('ann', 'old hash');
                CREATE TABLE edit_receipts(board TEXT, user TEXT, key TEXT, revision INTEGER, PRIMARY KEY(board, user, key));
                INSERT INTO boards VALUES($board, 'Old', '2026-01-01T00:00:00.0000000+00:00', 4, $file);
                INSERT INTO owners VALUES($board, 'ann');
                INSERT INTO sessions VALUES($hash, 'ann', 4102444800);
                """;
            command.Parameters.AddWithValue("$board", Board);
            command.Parameters.AddWithValue("$file", Encoding.UTF8.GetBytes(file.ToJsonString()));
            command.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
            command.ExecuteNonQuery();
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var board = Assert.Single((await client.GetFromJsonAsync<JsonArray>("/api/boards"))!);
        Assert.Equal(4, (long)board!["seq"]!);
        var snapshot = await client.GetFromJsonAsync<JsonObject>($"/api/boards/{Board}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(image));
        Assert.Equal(hash, (string)snapshot!["items"]![0]!["asset"]!);
        Assert.Equal("grid", (string)snapshot["background"]!);
        Assert.Equal(image, await client.GetByteArrayAsync($"/api/boards/{Board}/assets/{hash}"));
    }

    /// <summary>Joins the board by its draw link as a new guest and returns the guest's client and token.</summary>
    private async Task<(HttpClient Client, string Token)> JoinByLink(HttpClient owner, string name)
    {
        var link = await (await owner.PostAsync($"/api/boards/{Board}/link", null)).Content.ReadFromJsonAsync<JsonObject>();
        var joined = await factory.CreateClient().PostAsJsonAsync("/api/join", new { token = (string)link!["token"]!, name });
        joined.EnsureSuccessStatusCode();
        var token = (string)(await joined.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, token);
    }

    [Fact]
    public async Task GuestsOfADrawLinkWorkOnThatBoardAndMakeNothingElse()
    {
        var (ann, _) = await SignUp("ann");
        await CreateBoard(ann);
        var (guest, _) = await JoinByLink(ann, "Visitor");

        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/boards/{Board}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.PostAsync($"/api/boards/{Board}/changes", new StringContent(Put("g1", "seed"), Encoding.UTF8, "application/json"))).StatusCode);
        var other = "ffeeddccbbaa99887766554433221100";
        var make = await guest.PutAsync($"/api/boards/{other}", JsonContent.Create(new { title = "Mine", background = "grid", items = Array.Empty<object>() }));
        Assert.Equal(HttpStatusCode.Forbidden, make.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostAsJsonAsync("/api/folders", new { name = "Everyone" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostAsync("/api/templates?name=Spam", JsonContent.Create(new { format = "inkboard", version = 1, items = Array.Empty<object>() }))).StatusCode);
        Assert.DoesNotContain((await guest.GetFromJsonAsync<JsonArray>("/api/boards"))!, board => (string)board!["id"]! == other);
        Assert.Empty((await ann.GetFromJsonAsync<JsonObject>("/api/community"))!["templates"]!.AsArray());
    }

    [Fact]
    public async Task TurningOffTheDrawLinkDisconnectsGuestsAlreadyOnTheBoard()
    {
        var (ann, _) = await SignUp("ann");
        await CreateBoard(ann);
        var (_, guestToken) = await JoinByLink(ann, "Visitor");
        var socket = await Connect(guestToken, null);
        await NextOf(socket, "snapshot");

        Assert.Equal(HttpStatusCode.NoContent, (await ann.DeleteAsync($"/api/boards/{Board}/link")).StatusCode);
        while (await Next(socket) is not null) { }
        Assert.Equal((WebSocketCloseStatus)4403, socket.CloseStatus);
    }

    [Fact]
    public async Task TakingSomeoneOffABoardDisconnectsThemButNotTheOthers()
    {
        var (ann, annToken) = await SignUp("ann");
        var (_, bobToken) = await SignUp("bob");
        var (_, cidToken) = await SignUp("cid");
        await CreateBoard(ann);
        foreach (var name in new[] { "bob", "cid" })
            Assert.Equal(HttpStatusCode.NoContent, (await ann.PostAsJsonAsync($"/api/boards/{Board}/people", new { name })).StatusCode);
        var bob = await Connect(bobToken, null);
        var cid = await Connect(cidToken, null);
        var owner = await Connect(annToken, null);
        await NextOf(bob, "snapshot");
        await NextOf(cid, "snapshot");
        await NextOf(owner, "snapshot");

        Assert.Equal(HttpStatusCode.NoContent, (await ann.DeleteAsync($"/api/boards/{Board}/people/bob")).StatusCode);
        while (await Next(bob) is not null) { }
        Assert.Equal((WebSocketCloseStatus)4403, bob.CloseStatus);

        await Send(cid, """{"type":"ping"}""");
        await NextOf(cid, "pong");
        await Send(owner, """{"type":"ping"}""");
        await NextOf(owner, "pong");
    }

    [Fact]
    public async Task ImagesOnOneBoardStopAtTheBoardQuota()
    {
        var (ann, _) = await SignUp("ann");
        await CreateBoard(ann);
        await factory.Services.GetRequiredService<IBoardStore>().AddAsset(
            Board, new AssetInfo(new string('a', 64), "image/png", BoardLimits.MaxBoardAssetBytes), default);
        var png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 1, 2, 3 };
        var hash = Convert.ToHexStringLower(SHA256.HashData(png));
        var content = new ByteArrayContent(png);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await ann.PutAsync($"/api/boards/{Board}/assets/{hash}", content)).StatusCode);
    }
}
