using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Domain.Entities;

namespace Application.FunctionalTests.Live;

/// <summary>Cursors and ink in flight reach the others while the sender's own changes are still being stored.</summary>
public class SignalTests
{
    private const string Board = "00112233445566778899aabbccddeeff";
    private ServerFactory server = null!;
    private WebApplicationFactory<Program> factory = null!;

    [SetUp]
    public async Task SetUp()
    {
        await FunctionalTestSetup.DbResetter.ResetAsync();
        server = new ServerFactory(openRegistration: true);
        factory = server.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddScoped<ISaveChangesInterceptor, SlowChanges>()));
    }

    [TearDown]
    public async Task TearDown()
    {
        await factory.DisposeAsync();
        await server.DisposeAsync();
    }

    [Test]
    public async Task SignalsDoNotWaitForTheSendersChanges()
    {
        var client = factory.CreateClient();
        var registered = await client.PostAsJsonAsync("/api/register", new { name = "ann", password = "correct horse battery" });
        var token = (string)(await registered.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await client.PutAsync($"/api/boards/{Board}", JsonContent.Create(new { title = "Physics", background = "grid", items = Array.Empty<object>() })))
            .EnsureSuccessStatusCode();

        var drawing = await Connect(token);
        var watching = await Connect(token);

        await Send(drawing, """{"type":"submit","change":{"id":"c1","ops":[{"op":"put","item":{"id":"a","z":1}}]}}""");
        await Send(drawing, """{"type":"signal","data":{"cursor":{"x":1,"y":2}}}""");

        var first = await Next(watching, "signal", "change");
        ((string)first["type"]!).ShouldBe("signal");
        ((string)(await Next(watching, "change"))["change"]!["id"]!).ShouldBe("c1");
    }

    private async Task<WebSocket> Connect(string token)
    {
        var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(new Uri($"ws://localhost/api/boards/{Board}/live"), default);
        await Send(socket, new JsonObject { ["type"] = "hello", ["protocol"] = 1, ["token"] = token, ["after"] = 0 }.ToJsonString());
        await Next(socket, "ready");
        return socket;
    }

    private static Task Send(WebSocket socket, string json) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, default);

    private static async Task<JsonObject> Next(WebSocket socket, params string[] types)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("The socket closed.");
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);
            var frame = JsonNode.Parse(message.ToArray())!.AsObject();
            if (types.Contains((string)frame["type"]!)) return frame;
        }
    }

    /// <summary>A slow store: every board change takes a second to save.</summary>
    private sealed class SlowChanges : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<BoardChange>().Any(entry => entry.State == EntityState.Added))
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            return result;
        }
    }
}
