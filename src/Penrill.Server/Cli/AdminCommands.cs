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

    public string? Positional(int index)
    {
        var pos = 0;
        for (var i = 0; i < Args.Length; i++)
        {
            if (Args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (Args[i] is "--uses" or "--days" or "--revoke" or "--password" or "--owner" or "--delete" or "--role") i++;
                continue;
            }
            if (pos == index) return Args[i];
            pos++;
        }
        return null;
    }
}

/// <summary>One verb of <c>penrill-server</c> (Command pattern). The CLI only forwards arguments; the service runs the command.</summary>
public sealed record CliCommand(string Name, string Usage, string Summary, Func<CliContext, Task<IResult>> Run);

public sealed record CliUserDto(string Name, string Role, bool Disabled, DateTimeOffset? Created);
public sealed record CliDeviceDto(string Id, string User, string Name, DateTimeOffset Created, DateTimeOffset? LastUsed);
public sealed record CliBoardDto(string Id, string Title, string Owner, long Seq, DateTimeOffset Updated);
public sealed record CliInviteDto(string Id, string Role, int UsesLeft, DateTimeOffset? Expires, string CreatedBy);

public sealed record CliStateDto(
    string ServerName, string? Email, string Version, string Origin, string ConnectLink,
    string? Pin, string? ShortPin, bool Lan, bool Unclaimed,
    IReadOnlyList<CliUserDto> Users, IReadOnlyList<CliDeviceDto> Devices,
    IReadOnlyList<CliBoardDto> Boards, IReadOnlyList<CliInviteDto> Invites);

public sealed record CliClaimRequest(string Name, string Password, string ServerName, string Email);

public static class AdminCommands
{
    private const string By = "cli";

    public static readonly IReadOnlyList<CliCommand> All =
    [
        new("configure", "configure", "Interactive setup wizard and server management menu", Status),
        new("status", "status", "Address, certificate, users and boards", Status),
        new("setup", "setup", "Print a new setup link while the server has no owner", Setup),
        new("link", "link", "Print a link that connects a Penrill editor to this server", Link),
        new("invite", "invite [--admin] [--uses N] [--days N]", "Print an invite link for a new account", Invite),
        new("users", "users", "List accounts", Users),
        new("create-user", "create-user USER [--admin] [--password PASS]", "Create a new account directly", CreateUser),
        new("set-password", "set-password USER [PASSWORD]", "Set a new password for an account immediately", SetPassword),
        new("reset-password", "reset-password USER", "Print a one-hour link to set a new password", Reset),
        new("delete-user", "delete-user USER", "Delete an account and revoke its sessions", DeleteUser),
        new("role", "role USER member|admin", "Change an account's role", SetRole),
        new("disable", "disable USER", "Disable an account and sign it out everywhere", c => SetDisabled(c, true)),
        new("enable", "enable USER", "Enable an account again", c => SetDisabled(c, false)),
        new("devices", "devices [--revoke ID]", "List linked devices, or revoke one", Devices),
        new("boards", "boards [--owner ID USER] [--delete ID]", "List boards, change a board's owner, or delete a board", Boards),
        new("rename", "rename NAME", "Change the server name shown in editors", Rename),
        new("email", "email ADDRESS", "Set the administrator contact email", Email),
        new("backup", "backup [FILE]", "Write a backup archive of all boards and accounts", Backup),
        new("factory-reset", "factory-reset [--yes]", "Wipe all accounts and boards and restart fresh", Status),
        new("uninstall", "uninstall [--purge] [--yes]", "Uninstall Penrill Server (--purge removes all boards and data)", Status)
    ];

    public static void Map(WebApplication app)
    {
        var cli = app.MapGroup("/cli").AllowAnonymous().AddEndpointFilter(async (context, next) =>
            context.HttpContext.Features.Get<AdminSocketFeature>() is null ? Results.NotFound() : await next(context));
        foreach (var command in All)
            cli.MapPost("/" + command.Name, (string[] args, HttpContext http) => command.Run(new CliContext(args, http.RequestServices, http.RequestAborted)));

        cli.MapGet("/json/state", async (AdminService admin, GrantService grants, ServerAddress address, ConnectLinks links, CancellationToken cancel) =>
        {
            var origin = address.DefaultOrigin;
            var users = (await admin.Users(cancel))
                .Select(u => new CliUserDto(u.Name, u.Role.ToString().ToLowerInvariant(), u.Disabled, u.Created))
                .ToList();
            var devices = (await admin.Devices(cancel))
                .Select(d => new CliDeviceDto(d.Id, d.User, d.Name, d.Created, d.LastUsed))
                .ToList();
            var boards = (await admin.Boards(cancel))
                .Select(b => new CliBoardDto(b.Id, b.Title, b.Owner, b.Seq, b.Updated))
                .ToList();
            var invites = (await admin.Invites(cancel))
                .Select(i => new CliInviteDto(i.Id, i.Subject.ToLowerInvariant(), i.UsesLeft, i.Expires, i.CreatedBy))
                .ToList();
            return Results.Ok(new CliStateDto(
                await admin.ServerName(cancel),
                await admin.ContactEmail(cancel),
                typeof(AdminCommands).Assembly.GetName().Version?.ToString(3) ?? "0.3.0",
                origin,
                links.For(origin, address.Pin),
                address.Pin,
                address.ShortPin,
                address.Mode == AddressMode.Lan,
                await grants.Unclaimed(cancel),
                users,
                devices,
                boards,
                invites));
        });

        cli.MapPost("/json/claim", async (CliClaimRequest req, GrantService grants, AdminService admin, CancellationToken cancel) =>
        {
            if (AdminService.CleanEmail(req.Email) is null) return await Fail("Invalid email address.");
            if (req.ServerName.Trim() is not { Length: > 0 and <= AdminService.MaxServerName }) return await Fail("Server name must be 1 to 64 characters.");
            if (await grants.CreateClaim(cancel) is not { } token) return await Fail("This server already has an owner.");
            var result = await grants.Claim(token, req.Name, req.Password, cancel);
            if (result.Status != RedeemStatus.Ok) return await Fail($"Could not create owner account ({result.Status}).");
            await admin.Rename(req.ServerName, cancel);
            await admin.SetContactEmail(req.Email, cancel);
            return await Text($"Server claimed by {result.User}.");
        });

        cli.MapPost("/delete-invite", async (string[] args, AdminService admin, CancellationToken cancel) =>
            args.Length > 0 && await admin.DeleteInvite(args[0], cancel) ? await Text("Invite deleted.") : await Fail("No such invite."));
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

    private static async Task<IResult> CreateUser(CliContext c)
    {
        var name = c.Positional(0);
        var password = c.Option("--password") ?? c.Positional(1);
        var role = c.Flag("--admin") ? Role.Admin : Role.Member;
        var result = await c.Get<AdminService>().CreateUser(name, password, role, c.Cancel);
        return result.Status switch
        {
            SignInStatus.Ok => await Text($"Created account '{result.Name}' ({role.ToString().ToLowerInvariant()})."),
            SignInStatus.Taken => await Fail($"Account '{name}' already exists."),
            _ => await Fail("Invalid username (3-32 chars: a-z, 0-9, _, -) or password (10-256 chars).")
        };
    }

    private static async Task<IResult> SetPassword(CliContext c)
    {
        var name = c.Positional(0);
        var password = c.Option("--password") ?? c.Positional(1);
        if (!AccountService.IsPassword(password)) return await Fail("Password must be 10 to 256 characters.");
        return await c.Get<AdminService>().SetPassword(name, password, c.Cancel)
            ? await Text($"Password updated for '{AccountService.Normalize(name)}' and existing sessions signed out.")
            : await Fail("No such account.");
    }

    private static async Task<IResult> DeleteUser(CliContext c)
    {
        var name = c.Positional(0);
        return await c.Get<AdminService>().DeleteUser(name, c.Cancel)
            ? await Text($"Deleted account '{AccountService.Normalize(name)}' and revoked all sessions.")
            : await Fail("No such account, or it is the server owner.");
    }

    private static async Task<IResult> SetRole(CliContext c)
    {
        var name = c.Positional(0);
        var roleText = c.Positional(1);
        if (!Enum.TryParse<Role>(roleText, ignoreCase: true, out var role) || role is not (Role.Member or Role.Admin))
            return await Fail("Specify role: member or admin.");
        return await c.Get<AdminService>().SetRole(name, role, c.Cancel)
            ? await Text($"Updated '{AccountService.Normalize(name)}' role to {role.ToString().ToLowerInvariant()}.")
            : await Fail("No such account, or it is the server owner.");
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

    private static async Task<IResult> Boards(CliContext c)
    {
        var admin = c.Get<AdminService>();
        if (c.Option("--delete") is { } deleteId)
            return await admin.DeleteBoard(deleteId, c.Cancel) ? await Text($"Deleted board {deleteId}.") : await Fail("No such board.");
        if (c.Option("--owner") is { } boardId)
        {
            var user = c.Positional(0);
            return await admin.SetBoardOwner(boardId, user, c.Cancel)
                ? await Text($"Assigned board {boardId} to {AccountService.Normalize(user)}.")
                : await Fail("No such board or account.");
        }
        var text = new StringBuilder();
        foreach (var board in await admin.Boards(c.Cancel))
        {
            var owner = string.IsNullOrEmpty(board.Owner) ? "(unassigned)" : board.Owner;
            text.AppendLine($"{board.Id}  {owner,-20} {board.Title,-32} updated {board.Updated:yyyy-MM-dd}");
        }
        return await Text(text.Length == 0 ? "No boards yet." : text.ToString());
    }

    private static async Task<IResult> Rename(CliContext c) =>
        await c.Get<AdminService>().Rename(string.Join(' ', c.Args), c.Cancel) ? await Text("Renamed.") : await Fail("Use 1 to 64 characters.");

    private static async Task<IResult> Email(CliContext c) =>
        await c.Get<AdminService>().SetContactEmail(c.Positional(0), c.Cancel) ? await Text("Contact email updated.") : await Fail("Enter a valid email address.");

    private static async Task<IResult> Backup(CliContext c)
    {
        var buffer = new MemoryStream();
        await c.Get<AdminService>().Backup(buffer, c.Cancel);
        return Results.Bytes(buffer.ToArray(), "application/zip");
    }
}
