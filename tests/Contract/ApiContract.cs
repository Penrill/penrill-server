using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Penrill.Application.Common.Interfaces;
using Penrill.Domain.Constants;
using Penrill.Domain.Entities;
using Shouldly;

namespace Penrill.Contract.Tests;

/// <summary>
/// What editors rely on in <c>/api</c>: routes, JSON shapes, status codes, <c>reason</c> words and the live protocol
/// with its close codes. A host derives from this with a factory that has open registration and an empty store per
/// test; the tests themselves must pass unchanged on every host.
/// </summary>
public abstract class ApiContract<TProgram> where TProgram : class
{
    protected const string Board = "00112233445566778899aabbccddeeff";
    protected const string Password = "correct horse battery";

    private WebApplicationFactory<TProgram>? _factory;

    protected WebApplicationFactory<TProgram> Factory => _factory ?? throw new InvalidOperationException("No factory before SetUp.");

    /// <summary>A host with <c>PENRILL_OPEN_REGISTRATION=true</c> and no data.</summary>
    protected abstract Task<WebApplicationFactory<TProgram>> CreateFactoryAsync();

    /// <summary>Clears what <see cref="CreateFactoryAsync"/> made, after the factory is disposed.</summary>
    protected virtual Task CleanUpAsync() => Task.CompletedTask;

    [SetUp]
    public async Task StartHost() => _factory = await CreateFactoryAsync();

    [TearDown]
    public async Task StopHost()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        _factory = null;
        await CleanUpAsync();
    }

    protected async Task<(HttpClient Client, string Token)> SignUp(string name)
    {
        var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/register", new { name, password = Password });
        response.EnsureSuccessStatusCode();
        var token = (string)(await response.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, token);
    }

    protected static Task<HttpResponseMessage> CreateBoard(HttpClient client) =>
        client.PutAsync($"/api/boards/{Board}", JsonContent.Create(new { title = "Physics", background = "grid", items = new[] { new { id = "seed", z = 1 } } }));

    protected static string Put(string changeId, string itemId) =>
        $$$"""{"id":"{{{changeId}}}","ops":[{"op":"put","item":{"id":"{{{itemId}}}","z":2}}]}""";

    protected async Task<WebSocket> Connect(string token, long? after)
    {
        var socket = await Factory.Server.CreateWebSocketClient().ConnectAsync(new Uri($"ws://localhost/api/boards/{Board}/live"), default);
        var hello = new JsonObject { ["type"] = "hello", ["protocol"] = 1, ["token"] = token, ["after"] = after };
        await Send(socket, hello.ToJsonString());
        return socket;
    }

    protected static Task Send(WebSocket socket, string json) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, default);

    protected static async Task<JsonObject?> Next(WebSocket socket)
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

    protected static async Task<JsonObject> NextOf(WebSocket socket, string type)
    {
        while (await Next(socket) is { } frame)
            if ((string)frame["type"]! == type) return frame;
        throw new InvalidOperationException($"The socket closed before a {type} frame.");
    }

    protected static async Task DrainUntilClosed(WebSocket socket)
    {
        while (await Next(socket) is not null) { }
    }

    /// <summary>Joins the board by its draw link as a new guest and returns the guest's client and token.</summary>
    protected async Task<(HttpClient Client, string Token)> JoinByLink(HttpClient owner, string name)
    {
        var link = await (await owner.PostAsync($"/api/boards/{Board}/link", null)).Content.ReadFromJsonAsync<JsonObject>();
        var joined = await Factory.CreateClient().PostAsJsonAsync("/api/join", new { token = (string)link!["token"]!, name });
        joined.EnsureSuccessStatusCode();
        var token = (string)(await joined.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, token);
    }

    [Test]
    public async Task RequiresASession()
    {
        var response = await Factory.CreateClient().GetAsync("/api/boards");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task DescribesItselfToEditors()
    {
        var server = await Factory.CreateClient().GetFromJsonAsync<JsonObject>("/api/server");
        ((string)server!["service"]!).ShouldBe("penrill");
        ((string)server["registration"]!).ShouldBe("open");
        ((bool)server["link"]!).ShouldBeTrue();
        server.ContainsKey("name").ShouldBeTrue();
        server.ContainsKey("pin").ShouldBeTrue();
    }

    [Test]
    public async Task RegistersSignsInAndOut()
    {
        var (client, _) = await SignUp("ann");
        (await Factory.CreateClient().PostAsJsonAsync("/api/register", new { name = "Ann", password = Password })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Factory.CreateClient().PostAsJsonAsync("/api/register", new { name = "x", password = Password })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Factory.CreateClient().PostAsJsonAsync("/api/login", new { name = "ann", password = "wrong password" })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var login = await (await Factory.CreateClient().PostAsJsonAsync("/api/login", new { name = "ANN", password = Password })).Content.ReadFromJsonAsync<JsonObject>();
        ((string)login!["name"]!).ShouldBe("ann");
        ((string)login["token"]!).Length.ShouldBe(64);

        (await client.PostAsync("/api/logout", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync("/api/boards")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task OnlyTheOwnerDeletesABoardAndEveryoneOnItIsDisconnected()
    {
        var (ann, _) = await SignUp("ann");
        var (bob, bobToken) = await SignUp("bob");
        (await CreateBoard(ann)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ann.PostAsJsonAsync("/api/folders", new { name = "7-B" })).EnsureSuccessStatusCode();
        var folder = (string)(await ann.GetFromJsonAsync<JsonObject>("/api/community"))!["folders"]![0]!["id"]!;
        (await ann.PostAsJsonAsync("/api/members", new { folder, name = "bob" })).EnsureSuccessStatusCode();
        (await ann.PostAsJsonAsync("/api/share-board", new { folder, board = Board })).EnsureSuccessStatusCode();

        (await bob.DeleteAsync($"/api/boards/{Board}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var socket = await Connect(bobToken, null);
        await NextOf(socket, "snapshot");

        (await ann.DeleteAsync($"/api/boards/{Board}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await DrainUntilClosed(socket);
        socket.CloseStatus.ShouldBe((WebSocketCloseStatus)4404);
        (await ann.GetAsync($"/api/boards/{Board}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await bob.GetFromJsonAsync<JsonArray>("/api/boards"))!.ShouldBeEmpty();
        (await bob.GetFromJsonAsync<JsonObject>("/api/community"))!["boards"]!.AsArray().ShouldBeEmpty();
        (await ann.DeleteAsync($"/api/boards/{Board}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CreateBoard(ann)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Test]
    public async Task ListsBoardsWithTheirOwner()
    {
        var (ann, _) = await SignUp("ann");
        await CreateBoard(ann);
        var board = (await ann.GetFromJsonAsync<JsonArray>("/api/boards"))!.ShouldHaveSingleItem()!.AsObject();
        ((string)board["id"]!).ShouldBe(Board);
        ((string)board["title"]!).ShouldBe("Physics");
        ((long)board["seq"]!).ShouldBe(0);
        ((string)board["owner"]!).ShouldBe("ann");
        DateTimeOffset.TryParse((string)board["updated"]!, out _).ShouldBeTrue();
    }

    [Test]
    public async Task OwnersLetPeopleInByNameAndTakeThemOutAgain()
    {
        var (ann, _) = await SignUp("ann");
        var (bob, _) = await SignUp("bob");
        await CreateBoard(ann);
        var people = $"/api/boards/{Board}/people";

        (await bob.GetAsync($"/api/boards/{Board}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ann.PostAsJsonAsync(people, new { name = "nobody" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await bob.PostAsJsonAsync(people, new { name = "bob" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ann.PostAsJsonAsync(people, new { name = "Bob" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ann.PostAsJsonAsync(people, new { name = "bob" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ann.GetFromJsonAsync<string[]>(people))!.ShouldBe(["bob"]);

        (await bob.GetAsync($"/api/boards/{Board}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await bob.GetFromJsonAsync<JsonArray>("/api/boards"))!.ShouldHaveSingleItem();
        (await bob.GetAsync(people)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await bob.DeleteAsync($"{people}/bob")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await ann.DeleteAsync($"{people}/bob")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await bob.GetAsync($"/api/boards/{Board}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ann.GetFromJsonAsync<string[]>(people))!.ShouldBeEmpty();
    }

    [Test]
    public async Task AnyoneWithTheLinkDrawsWithoutAnAccountUntilItIsTurnedOff()
    {
        var (ann, _) = await SignUp("ann");
        await CreateBoard(ann);
        var link = $"/api/boards/{Board}/link";
        var anonymous = Factory.CreateClient();

        (await anonymous.PostAsJsonAsync("/api/join", new { token = "nope", name = "Kid" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var (bob, _) = await SignUp("bob");
        (await bob.GetAsync(link)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ann.GetFromJsonAsync<JsonObject>(link))!["token"].ShouldBeNull();
        var token = (string)(await (await ann.PostAsync(link, null)).Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        ((string)(await (await ann.PostAsync(link, null)).Content.ReadFromJsonAsync<JsonObject>())!["token"]!).ShouldBe(token);
        ((string)(await ann.GetFromJsonAsync<JsonObject>(link))!["token"]!).ShouldBe(token);
        (await anonymous.PostAsJsonAsync("/api/join", new { token })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var joined = (await (await anonymous.PostAsJsonAsync("/api/join", new { token, name = "Kid Ó" })).Content.ReadFromJsonAsync<JsonObject>())!;
        ((string)joined["name"]!).ShouldStartWith("kid-");
        ((string)joined["board"]!).ShouldBe(Board);
        var kid = Factory.CreateClient();
        kid.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (string)joined["token"]!);
        (await kid.GetAsync($"/api/boards/{Board}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await kid.GetAsync(link)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        // Opening the link again on the same device keeps the account it already has.
        var again = (await (await kid.PostAsJsonAsync("/api/join", new { token, name = "Kid" })).Content.ReadFromJsonAsync<JsonObject>())!;
        ((string)again["name"]!).ShouldBe((string)joined["name"]!);
        again["token"].ShouldBeNull();
        // So does bob, who has a real account: he just gets the board.
        ((string)(await (await bob.PostAsJsonAsync("/api/join", new { token })).Content.ReadFromJsonAsync<JsonObject>())!["name"]!).ShouldBe("bob");
        (await bob.GetAsync($"/api/boards/{Board}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ann.DeleteAsync(link)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await kid.GetAsync($"/api/boards/{Board}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await bob.GetAsync($"/api/boards/{Board}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ann.GetFromJsonAsync<JsonObject>(link))!["token"].ShouldBeNull();
        (await anonymous.PostAsJsonAsync("/api/join", new { token, name = "Kid" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task CreatingABoardTwiceIsSafe()
    {
        var (ann, _) = await SignUp("ann");
        (await CreateBoard(ann)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var again = await CreateBoard(ann);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        ((bool)(await again.Content.ReadFromJsonAsync<JsonObject>())!["created"]!).ShouldBeFalse();
        var (bob, _) = await SignUp("bob");
        (await CreateBoard(bob)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await bob.GetAsync($"/api/boards/{Board}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task RefusesBoardsThatAreNotJsonOrNotBoards()
    {
        var (ann, _) = await SignUp("ann");
        (await ann.PutAsync($"/api/boards/{Board}", new StringContent("{}", Encoding.UTF8, "text/plain"))).StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (await ann.PutAsync($"/api/boards/{Board}", new StringContent("""{"title":"T"}""", Encoding.UTF8, "application/json"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ann.PutAsync("/api/boards/not-a-board", JsonContent.Create(new { title = "T", background = "dots", items = Array.Empty<object>() }))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task SubmitsOverHttpAndCatchesUp()
    {
        var (ann, _) = await SignUp("ann");
        await CreateBoard(ann);
        var first = await ann.PostAsync($"/api/boards/{Board}/changes", new StringContent(Put("c1", "a"), Encoding.UTF8, "application/json"));
        ((long)(await first.Content.ReadFromJsonAsync<JsonObject>())!["seq"]!).ShouldBe(1);
        var repeated = await ann.PostAsync($"/api/boards/{Board}/changes", new StringContent(Put("c1", "a"), Encoding.UTF8, "application/json"));
        ((long)(await repeated.Content.ReadFromJsonAsync<JsonObject>())!["seq"]!).ShouldBe(1);
        var malformed = await ann.PostAsync($"/api/boards/{Board}/changes", new StringContent("""{"id":"c2","ops":[]}""", Encoding.UTF8, "application/json"));
        malformed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ((string)(await malformed.Content.ReadFromJsonAsync<JsonObject>())!["reason"]!).ShouldBe("invalid");

        var changes = await ann.GetFromJsonAsync<JsonObject>($"/api/boards/{Board}/changes?after=0");
        ((long)changes!["seq"]!).ShouldBe(1);
        var change = changes["changes"]!.AsArray().ShouldHaveSingleItem()!;
        ((string)change["author"]!).ShouldBe("ann");
        ((string)change["change"]!["id"]!).ShouldBe("c1");
        (await ann.GetAsync($"/api/boards/{Board}/changes?after=7")).StatusCode.ShouldBe(HttpStatusCode.Gone);

        var snapshot = await ann.GetFromJsonAsync<JsonObject>($"/api/boards/{Board}");
        snapshot!["items"]!.AsArray().Count.ShouldBe(2);
        ((string)snapshot["title"]!).ShouldBe("Physics");
        ((string)snapshot["id"]!).ShouldBe(Board);
        ((long)snapshot["seq"]!).ShouldBe(1);
    }

    [Test]
    public async Task LiveClientsShareChangesAndResumeAfterADrop()
    {
        var (ann, token) = await SignUp("ann");
        await CreateBoard(ann);

        var tablet = await Connect(token, after: 0);
        var welcome = (await Next(tablet))!;
        ((string)welcome["type"]!).ShouldBe("welcome");
        ((string)welcome["user"]!).ShouldBe("ann");
        ((long)(await NextOf(tablet, "ready"))["seq"]!).ShouldBe(0);
        var laptop = await Connect(token, after: null);
        (await NextOf(laptop, "snapshot"))["board"]!["items"]!.AsArray().ShouldHaveSingleItem();
        await NextOf(laptop, "ready");

        await Send(tablet, $$"""{"type":"submit","change":{{Put("c1", "a")}}}""");
        ((long)(await NextOf(tablet, "change"))["seq"]!).ShouldBe(1);
        ((string)(await NextOf(laptop, "change"))["change"]!["id"]!).ShouldBe("c1");

        await tablet.CloseAsync(WebSocketCloseStatus.NormalClosure, "", default);
        await Send(laptop, $$"""{"type":"submit","change":{{Put("c2", "b")}}}""");
        await NextOf(laptop, "change");

        var resumed = await Connect(token, after: 1);
        var missed = await NextOf(resumed, "changes");
        ((string)missed["changes"]![0]!["change"]!["id"]!).ShouldBe("c2");
        await NextOf(resumed, "ready");

        await Send(resumed, $$"""{"type":"submit","change":{{Put("c1", "a")}}}""");
        var ack = await NextOf(resumed, "ack");
        ((long)ack["seq"]!).ShouldBe(1);
        await Send(resumed, """{"type":"submit","change":{"id":"c3","ops":[{"op":"teleport"}]}}""");
        var reject = await NextOf(resumed, "reject");
        ((string)reject["id"]!).ShouldBe("c3");
        ((string)reject["reason"]!).ShouldBe("invalid");
        await Send(resumed, """{"type":"ping"}""");
        await NextOf(resumed, "pong");
    }

    [Test]
    public async Task LiveConnectionsNeedAValidSessionAndAccess()
    {
        var (ann, _) = await SignUp("ann");
        await CreateBoard(ann);
        var stranger = await Connect(new string('0', 64), after: null);
        (await Next(stranger)).ShouldBeNull();
        stranger.CloseStatus.ShouldBe((WebSocketCloseStatus)4401);

        var (_, bobToken) = await SignUp("bob");
        var bob = await Connect(bobToken, after: null);
        (await Next(bob)).ShouldBeNull();
        bob.CloseStatus.ShouldBe((WebSocketCloseStatus)4403);
    }

    [Test]
    public async Task SigningOutClosesTheLiveConnectionsOfThatSession()
    {
        var (ann, token) = await SignUp("ann");
        await CreateBoard(ann);
        var socket = await Connect(token, after: null);
        await NextOf(socket, "ready");
        (await ann.PostAsync("/api/logout", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await DrainUntilClosed(socket);
        socket.CloseStatus.ShouldBe((WebSocketCloseStatus)4401);
    }

    [Test]
    public async Task StoresImagesByTheirHash()
    {
        var (ann, _) = await SignUp("ann");
        await CreateBoard(ann);
        var png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 1, 2, 3 };
        var hash = Convert.ToHexStringLower(SHA256.HashData(png));
        static ByteArrayContent Image(byte[] bytes, string type = "image/png")
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(type);
            return content;
        }
        var mismatch = await ann.PutAsync($"/api/boards/{Board}/assets/{hash}", Image([1, 2]));
        mismatch.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ((string)(await mismatch.Content.ReadFromJsonAsync<JsonObject>())!["reason"]!).ShouldBe("hash-mismatch");
        (await ann.PutAsync($"/api/boards/{Board}/assets/{hash}", Image(png, "text/html"))).StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (await ann.PutAsync($"/api/boards/{Board}/assets/{hash}", Image(png))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var download = await ann.GetAsync($"/api/boards/{Board}/assets/{hash}");
        (await download.Content.ReadAsByteArrayAsync()).ShouldBe(png);
        download.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        download.Headers.CacheControl!.Private.ShouldBeTrue();
        (await ann.GetAsync($"/api/boards/{Board}/assets/{new string('b', 64)}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task GuestsOfADrawLinkWorkOnThatBoardAndMakeNothingElse()
    {
        var (ann, _) = await SignUp("ann");
        await CreateBoard(ann);
        var (guest, _) = await JoinByLink(ann, "Visitor");

        (await guest.GetAsync($"/api/boards/{Board}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await guest.PostAsync($"/api/boards/{Board}/changes", new StringContent(Put("g1", "seed"), Encoding.UTF8, "application/json"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var other = "ffeeddccbbaa99887766554433221100";
        var make = await guest.PutAsync($"/api/boards/{other}", JsonContent.Create(new { title = "Mine", background = "grid", items = Array.Empty<object>() }));
        make.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await guest.PostAsJsonAsync("/api/folders", new { name = "Everyone" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await guest.PostAsync("/api/templates?name=Spam", JsonContent.Create(new { format = "inkboard", version = 1, items = Array.Empty<object>() }))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await guest.GetFromJsonAsync<JsonArray>("/api/boards"))!.ShouldNotContain(board => (string)board!["id"]! == other);
        (await ann.GetFromJsonAsync<JsonObject>("/api/community"))!["templates"]!.AsArray().ShouldBeEmpty();
    }

    [Test]
    public async Task TurningOffTheDrawLinkDisconnectsGuestsAlreadyOnTheBoard()
    {
        var (ann, _) = await SignUp("ann");
        await CreateBoard(ann);
        var (_, guestToken) = await JoinByLink(ann, "Visitor");
        var socket = await Connect(guestToken, null);
        await NextOf(socket, "snapshot");

        (await ann.DeleteAsync($"/api/boards/{Board}/link")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await DrainUntilClosed(socket);
        socket.CloseStatus.ShouldBe((WebSocketCloseStatus)4403);
    }

    [Test]
    public async Task TakingSomeoneOffABoardDisconnectsThemButNotTheOthers()
    {
        var (ann, annToken) = await SignUp("ann");
        var (_, bobToken) = await SignUp("bob");
        var (_, cidToken) = await SignUp("cid");
        await CreateBoard(ann);
        foreach (var name in new[] { "bob", "cid" })
            (await ann.PostAsJsonAsync($"/api/boards/{Board}/people", new { name })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var bob = await Connect(bobToken, null);
        var cid = await Connect(cidToken, null);
        var owner = await Connect(annToken, null);
        await NextOf(bob, "snapshot");
        await NextOf(cid, "snapshot");
        await NextOf(owner, "snapshot");

        (await ann.DeleteAsync($"/api/boards/{Board}/people/bob")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await DrainUntilClosed(bob);
        bob.CloseStatus.ShouldBe((WebSocketCloseStatus)4403);

        await Send(cid, """{"type":"ping"}""");
        await NextOf(cid, "pong");
        await Send(owner, """{"type":"ping"}""");
        await NextOf(owner, "pong");
    }

    [Test]
    public async Task ImagesOnOneBoardStopAtTheBoardQuota()
    {
        var (ann, _) = await SignUp("ann");
        await CreateBoard(ann);
        using (var scope = Factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            context.BoardAssets.Add(new BoardAsset { BoardId = Board, Hash = new string('a', 64), ContentType = "image/png", Size = BoardLimits.MaxBoardAssetBytes });
            await context.SaveChangesAsync(default);
        }
        var png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 1, 2, 3 };
        var hash = Convert.ToHexStringLower(SHA256.HashData(png));
        var content = new ByteArrayContent(png);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        (await ann.PutAsync($"/api/boards/{Board}/assets/{hash}", content)).StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Test]
    public async Task FoldersShareBoardsAndTemplatesWithTheirMembers()
    {
        var (ann, _) = await SignUp("ann");
        var (bob, _) = await SignUp("bob");
        await CreateBoard(ann);
        (await ann.PostAsJsonAsync("/api/folders", new { name = "7-B" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var folder = (string)(await ann.GetFromJsonAsync<JsonObject>("/api/community"))!["folders"]![0]!["id"]!;
        (await bob.PostAsJsonAsync("/api/members", new { folder, name = "bob" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ann.PostAsJsonAsync("/api/members", new { folder, name = "nobody" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ann.PostAsJsonAsync("/api/members", new { folder, name = "bob" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await bob.PostAsJsonAsync("/api/share-board", new { folder, board = Board })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ann.PostAsJsonAsync("/api/share-board", new { folder, board = Board })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var file = new { format = "inkboard", version = 1, items = Array.Empty<object>() };
        (await ann.PostAsync($"/api/templates?name=Lab&folder={folder}", JsonContent.Create(new { title = "no" }))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var published = await (await ann.PostAsync($"/api/templates?name=Lab&folder={folder}", JsonContent.Create(file))).Content.ReadFromJsonAsync<JsonObject>();
        var id = (string)published!["id"]!;
        ((string)published["folder"]!).ShouldBe(folder);
        ((string)published["author"]!).ShouldBe("ann");
        var version = (long)published["updated"]!;

        var community = (await bob.GetFromJsonAsync<JsonObject>("/api/community"))!;
        ((string)community["folders"]![0]!["owner"]!).ShouldBe("ann");
        community["members"]!.AsArray().Select(m => (string)m!["name"]!).ShouldBe(["ann", "bob"]);
        var shared = community["boards"]!.AsArray().ShouldHaveSingleItem()!;
        ((string)shared["id"]!).ShouldBe(Board);
        ((string)shared["folder"]!).ShouldBe(folder);
        ((string)community["templates"]!.AsArray().ShouldHaveSingleItem()!["name"]!).ShouldBe("Lab");
        (await bob.GetAsync($"/api/boards/{Board}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await bob.GetAsync($"/api/templates/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await bob.PutAsync($"/api/templates/{id}?name=Mine", JsonContent.Create(file))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var edited = await (await ann.PutAsync($"/api/templates/{id}?name=Lab 2", JsonContent.Create(file))).Content.ReadFromJsonAsync<JsonObject>();
        ((long)edited!["updated"]!).ShouldBeGreaterThan(version);
        (await bob.DeleteAsync($"/api/templates/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ann.DeleteAsync($"/api/templates/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await bob.GetAsync($"/api/templates/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task LinksADeviceAfterApproval()
    {
        var (browser, _) = await SignUp("ann");
        var editor = Factory.CreateClient();
        var start = await (await editor.PostAsJsonAsync("/api/link/start", new { device = "MacBook", app = "penrill 0.14" })).Content.ReadFromJsonAsync<JsonObject>();
        var userCode = (string)start!["userCode"]!;
        var deviceCode = (string)start["deviceCode"]!;
        userCode.ShouldMatch("^[A-Z]{4}-[A-Z]{4}$");
        ((string)start["verifyUrlComplete"]!).ShouldEndWith("/link?code=" + userCode);
        ((string)start["verifyUrl"]!).ShouldEndWith("/link");
        ((int)start["expiresIn"]!).ShouldBeGreaterThan(0);

        var info = await browser.GetFromJsonAsync<JsonObject>("/api/link/" + userCode.ToLowerInvariant().Replace("-", " "));
        ((string)info!["device"]!).ShouldBe("MacBook");

        var poll = editor.PostAsJsonAsync("/api/link/poll", new { deviceCode });
        (await browser.PostAsJsonAsync("/api/link/" + userCode, new { allow = true })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var linked = await (await poll).Content.ReadFromJsonAsync<JsonObject>();
        ((string)linked!["name"]!).ShouldBe("ann");
        var token = (string)linked["token"]!;

        (await editor.PostAsJsonAsync("/api/link/poll", new { deviceCode })).StatusCode.ShouldBe(HttpStatusCode.Gone);
        var device = Factory.CreateClient();
        device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await device.GetAsync("/api/boards")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var devices = await device.GetFromJsonAsync<JsonArray>("/api/devices");
        var listed = devices!.ShouldHaveSingleItem()!;
        ((string)listed["name"]!).ShouldBe("MacBook");
        ((bool)listed["current"]!).ShouldBeTrue();
        (await browser.PostAsync($"/api/devices/{listed["id"]}/revoke", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await device.GetAsync("/api/boards")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
