using System.Text;
using System.Text.Json.Nodes;
using Application.Common.Interfaces;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Respawn;
using Respawn.Graph;
using Shared;
using Sync.Live;
using Sync.Protocol;

namespace Infrastructure.IntegrationTests.Live;

/// <summary>Board rooms on a real PostgreSQL database: ordering, duplicates, catching up, snapshots and slow members.</summary>
public class RoomTests
{
    private const string Board = "0123456789abcdef0123456789abcdef";
    private string _directory = null!;
    private string _connectionString = null!;
    private DistributedApplication? _app;
    private Respawner _respawner = null!;
    private IHost _host = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (Environment.GetEnvironmentVariable("PENRILL_TEST_DATABASE") is { Length: > 0 } database)
        {
            _connectionString = database;
        }
        else
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestAppHost>(
                args: [], configureBuilder: (options, _) => options.DisableDashboard = true, cts.Token);
            builder.Configuration["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true";
            _app = await builder.BuildAsync(cts.Token).WaitAsync(cts.Token);
            await _app.StartAsync(cts.Token).WaitAsync(cts.Token);
            await _app.ResourceNotifications.WaitForResourceHealthyAsync(Services.Database, cts.Token);
            _connectionString = (await _app.GetConnectionStringAsync(Services.Database, cts.Token))!;
        }

        _directory = Directory.CreateTempSubdirectory("penrill-room-").FullName;
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration["PENRILL_DATA_DIR"] = _directory;
        hostBuilder.Configuration[$"ConnectionStrings:{Services.Database}"] = _connectionString;
        hostBuilder.AddApplicationServices();
        hostBuilder.AddInfrastructureServices();
        hostBuilder.Services.AddScoped<IUser, NoUser>();
        _host = hostBuilder.Build();
        await _host.InitialiseDatabaseAsync();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        _respawner = await Respawner.CreateAsync(conn, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore = [new Table("__EFMigrationsHistory"), new Table("AspNetRoles")]
        });
    }

    [SetUp]
    public async Task SetUp()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await _respawner.ResetAsync(conn);

        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        context.Boards.Add(new Board { Id = Board, Title = "Board", Background = "dots", Snapshot = """[{"id":"seed","z":1}]"""u8.ToArray() });
        await context.SaveChangesAsync(default);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        _host?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private RoomRegistry Registry(int snapshotEvery = 500, int recent = 1000) =>
        new(_host.Services.GetRequiredService<IBoardStore>(),
            _host.Services.GetRequiredService<IBoardHistorySink>(),
            _host.Services.GetRequiredService<IRoomBackplane>(),
            new RoomOptions { SnapshotEvery = snapshotEvery, RecentChanges = recent, IdleTimeout = TimeSpan.Zero },
            TimeProvider.System, NullLoggerFactory.Instance);

    private static (Change Change, byte[] Body) Put(string changeId, string itemId)
    {
        var json = $$$"""{"id":"{{{changeId}}}","ops":[{"op":"put","item":{"id":"{{{itemId}}}","z":2}}]}""";
        ChangeCodec.TryParse(JsonNode.Parse(json), out var change).ShouldBeTrue();
        return (change!, Encoding.UTF8.GetBytes(json));
    }

    private static async Task<SubmitResult> Submit(BoardRoom room, string changeId, string itemId)
    {
        var (change, body) = Put(changeId, itemId);
        return await room.Submit(change, body, "ann", default);
    }

    [Test]
    public async Task OrdersChangesAndBroadcastsThemToEveryone()
    {
        var rooms = Registry();
        using var lease = (await rooms.Acquire(Board, default))!;
        var watcher = new RecordingMember();
        await lease.Room.Attach(watcher, after: 0, default);
        (await Submit(lease.Room, "c1", "a")).ShouldBe(new SubmitResult(SubmitStatus.Accepted, 1));
        (await Submit(lease.Room, "c2", "b")).ShouldBe(new SubmitResult(SubmitStatus.Accepted, 2));
        watcher.Types.ShouldBe(["ready", "change", "change"]);
        ((long)watcher.Frames[^1]["seq"]!).ShouldBe(2);
    }

    [Test]
    public async Task AcknowledgesARepeatedChangeWithoutApplyingItTwice()
    {
        var rooms = Registry();
        using (var lease = (await rooms.Acquire(Board, default))!)
        {
            await Submit(lease.Room, "c1", "a");
            (await Submit(lease.Room, "c1", "a")).ShouldBe(new SubmitResult(SubmitStatus.Duplicate, 1));
        }
        await rooms.EvictIdle();
        using var reloaded = (await rooms.Acquire(Board, default))!;
        (await Submit(reloaded.Room, "c1", "a")).ShouldBe(new SubmitResult(SubmitStatus.Duplicate, 1));
        reloaded.Room.Seq.ShouldBe(1);
    }

    [Test]
    public async Task JoiningClientsCatchUpWithADeltaOrASnapshot()
    {
        var rooms = Registry(snapshotEvery: 2, recent: 2);
        using var lease = (await rooms.Acquire(Board, default))!;
        for (var i = 1; i <= 5; i++) await Submit(lease.Room, "c" + i, "item" + i);

        var current = new RecordingMember();
        await lease.Room.Attach(current, after: 5, default);
        current.Types.ShouldBe(["ready"]);

        var behind = new RecordingMember();
        await lease.Room.Attach(behind, after: 3, default);
        behind.Types.ShouldBe(["changes", "ready"]);
        behind.Frames[0]["changes"]!.AsArray().Select(change => (long)change!["seq"]!).ShouldBe([4L, 5L]);

        var lost = new RecordingMember();
        await lease.Room.Attach(lost, after: 1, default);
        lost.Types.ShouldBe(["snapshot", "ready"]);
        lost.Frames[0]["board"]!["items"]!.AsArray().Count.ShouldBe(6);
    }

    [Test]
    public async Task ABoardReloadsFromItsSnapshotAndLog()
    {
        var rooms = Registry(snapshotEvery: 2);
        byte[] before;
        using (var lease = (await rooms.Acquire(Board, default))!)
        {
            for (var i = 1; i <= 5; i++) await Submit(lease.Room, "c" + i, "item" + i);
            before = await lease.Room.Snapshot(default);
        }
        await rooms.EvictIdle();
        using var reloaded = (await rooms.Acquire(Board, default))!;
        (await reloaded.Room.Snapshot(default)).ShouldBe(before);
    }

    [Test]
    public async Task DisconnectsAMemberThatCannotKeepUp()
    {
        var rooms = Registry();
        using var lease = (await rooms.Acquire(Board, default))!;
        var slow = new RecordingMember { Accepts = 1 };
        await lease.Room.Attach(slow, after: 0, default);
        await Submit(lease.Room, "c1", "a");
        slow.Closed.ShouldBe(LiveCloseReason.SlowConsumer);
    }

    [Test]
    public async Task AMissingBoardHasNoRoom()
    {
        var rooms = Registry();
        (await rooms.Acquire("ffffffffffffffffffffffffffffffff", default)).ShouldBeNull();
    }

    private sealed class NoUser : IUser
    {
        public string? Id => null;
        public List<string>? Roles => null;
        public string? CredentialId => null;
    }

    private sealed class RecordingMember : IRoomMember
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string UserId => "ann";
        public string User => "ann";
        public BoardPermission Permission => BoardPermission.Edit;
        public int Accepts { get; init; } = int.MaxValue;
        public List<JsonObject> Frames { get; } = [];
        public LiveCloseReason? Closed { get; private set; }
        public List<string> Types => [.. Frames.Select(frame => (string)frame["type"]!)];

        public bool TrySend(byte[] frame)
        {
            if (Frames.Count >= Accepts) return false;
            Frames.Add(JsonNode.Parse(frame)!.AsObject());
            return true;
        }

        public void Close(LiveCloseReason reason) => Closed = reason;
    }
}
