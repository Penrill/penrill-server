using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Penrill.Core.Accounts;
using Penrill.Core.Admin;
using Penrill.Server.Hosting;

namespace Penrill.Server.Cli;

/// <summary>Set on connections that arrived over the admin Unix socket. Only those reach <c>/cli</c>.</summary>
public sealed class AdminSocketFeature;

public sealed record CliContext(string[] Args, IServiceProvider Services, CancellationToken Cancel)
{
    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public string? Option(string name)
    {
        var index = Array.IndexOf(Args, name);
        return index >= 0 && index + 1 < Args.Length ? Args[index + 1] : null;
    }

    public bool Flag(string name) => Args.Contains(name);

    public string? Positional(int index) => Args.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).Skip(index).FirstOrDefault();
}

/// <summary>One verb of <c>penrill-server</c> (Command pattern). The CLI only forwards arguments; the service runs the command.</summary>
public sealed record CliCommand(string Name, string Usage, string Summary, Func<CliContext, Task<IResult>> Run);

public static class AdminCommands
{
    private const string By = "cli";

    public static readonly IReadOnlyList<CliCommand> All =
    [
        new("status", "status", "Address, certificate, users and boards", Status),
        new("setup", "setup", "Print a new setup link while the server has no owner", Setup),
        new("link", "link", "Print a link that connects a Penrill editor to this server", Link),
        new("invite", "invite [--admin] [--uses N] [--days N]", "Print an invite link for a new account", Invite),
        new("reset-password", "reset-password USER", "Print a one-hour link to set a new password", Reset),
        new("users", "users", "List accounts", Users),
        new("disable", "disable USER", "Disable an account and sign it out everywhere", c => SetDisabled(c, true)),
        new("enable", "enable USER", "Enable an account again", c => SetDisabled(c, false)),
        new("devices", "devices [--revoke ID]", "List linked devices, or revoke one", Devices),
        new("rename", "rename NAME", "Change the server name shown in editors", Rename),
        new("backup", "backup FILE", "Write a backup archive of all boards and accounts", Backup)
    ];

    public static void Map(WebApplication app)
    {
        var cli = app.MapGroup("/cli").AllowAnonymous().AddEndpointFilter(async (context, next) =>
            context.HttpContext.Features.Get<AdminSocketFeature>() is null ? Results.NotFound() : await next(context));
        foreach (var command in All)
            cli.MapPost("/" + command.Name, (string[] args, HttpContext http) => command.Run(new CliContext(args, http.RequestServices, http.RequestAborted)));
    }

    private static Task<IResult> Text(string text) => Task.FromResult(Results.Text(text.EndsWith('\n') ? text : text + "\n"));

    private static Task<IResult> Fail(string text) => Task.FromResult(Results.Text(text + "\n", statusCode: 400));

    private static string WithQr(string title, string url) => $"{title}\n\n  {url}\n\n{Qr.Terminal(url)}";

    private static async Task<IResult> Status(CliContext c)
    {
        var admin = c.Get<AdminService>();
        var address = c.Get<ServerAddress>();
        var counts = await admin.Counts(c.Cancel);
        var claimed = !await c.Get<GrantService>().Unclaimed(c.Cancel);
        var text = new StringBuilder()
            .AppendLine($"Name:         {await admin.ServerName(c.Cancel)}")
            .AppendLine($"Version:      {typeof(AdminCommands).Assembly.GetName().Version?.ToString(3)}")
            .AppendLine($"Address:      {address.DefaultOrigin}")
            .AppendLine($"Mode:         {(address.Mode == AddressMode.Lan ? "this network only" : "local")}");
        if (address.ShortPin is { } pin) text.AppendLine($"Certificate:  {pin}  ({address.Pin})");
        text.AppendLine($"Owner:        {(claimed ? "set" : "none yet, run: penrill-server setup")}")
            .AppendLine($"Accounts:     {counts.Users}")
            .AppendLine($"Boards:       {counts.Boards}")
            .AppendLine($"Devices:      {counts.Devices}");
        return await Text(text.ToString());
    }

    private static async Task<IResult> Setup(CliContext c) =>
        await c.Get<SetupAnnouncer>().Message(c.Cancel) is { } message
            ? await Text(message)
            : await Text("This server already has an owner. To add people, run: penrill-server invite");

    private static Task<IResult> Link(CliContext c)
    {
        var address = c.Get<ServerAddress>();
        return Text($"In Penrill, open Sync › Your server and enter {address.DefaultOrigin}\n\n" +
            WithQr("Or open this link, or scan it on a tablet:", c.Get<ConnectLinks>().For(address.DefaultOrigin, address.Pin)));
    }

    private static async Task<IResult> Invite(CliContext c)
    {
        var uses = int.TryParse(c.Option("--uses"), CultureInfo.InvariantCulture, out var n) ? n : 1;
        var days = double.TryParse(c.Option("--days"), CultureInfo.InvariantCulture, out var d) ? d : 7;
        var invite = await c.Get<AdminService>().Invite(By, c.Flag("--admin") ? Role.Admin : Role.Member, TimeSpan.FromDays(days), uses, c.Cancel);
        var url = $"{c.Get<ServerAddress>().DefaultOrigin}/invite/{invite.Token}";
        return await Text(WithQr($"Invite link, {(uses == 1 ? "single use" : $"{uses} uses")}, valid for {days} days:", url));
    }

    private static async Task<IResult> Reset(CliContext c)
    {
        if (await c.Get<AdminService>().ResetLink(By, c.Positional(0), c.Cancel) is not { } reset) return await Fail("No such account.");
        return await Text(WithQr($"Password reset link for {reset.Grant.Subject}, valid for one hour:",
            $"{c.Get<ServerAddress>().DefaultOrigin}/reset/{reset.Token}"));
    }

    private static async Task<IResult> Users(CliContext c)
    {
        var text = new StringBuilder();
        foreach (var user in await c.Get<AdminService>().Users(c.Cancel))
            text.AppendLine($"{user.Name,-32} {user.Role,-7} {(user.Disabled ? "disabled" : "")}");
        return await Text(text.Length == 0 ? "No accounts yet." : text.ToString());
    }

    private static async Task<IResult> SetDisabled(CliContext c, bool disabled) =>
        await c.Get<AdminService>().SetDisabled(c.Positional(0), disabled, c.Cancel)
            ? await Text(disabled ? "Disabled and signed out everywhere." : "Enabled.")
            : await Fail("No such account, or it is the owner.");

    private static async Task<IResult> Devices(CliContext c)
    {
        var admin = c.Get<AdminService>();
        if (c.Option("--revoke") is { } id)
            return await admin.RevokeDevice(id, c.Cancel) ? await Text("Revoked.") : await Fail("No such device.");
        var text = new StringBuilder();
        foreach (var device in await admin.Devices(c.Cancel))
            text.AppendLine($"{device.Id}  {device.User,-20} {device.Name,-32} last used {device.LastUsed?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "never"}");
        return await Text(text.Length == 0 ? "No linked devices." : text.ToString());
    }

    private static async Task<IResult> Rename(CliContext c) =>
        await c.Get<AdminService>().Rename(string.Join(' ', c.Args), c.Cancel) ? await Text("Renamed.") : await Fail("Use 1 to 64 characters.");

    private static async Task<IResult> Backup(CliContext c)
    {
        var buffer = new MemoryStream();
        await c.Get<AdminService>().Backup(buffer, c.Cancel);
        return Results.Bytes(buffer.ToArray(), "application/zip");
    }
}
