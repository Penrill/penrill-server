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
                if (Args[i] is "--uses" or "--days" or "--revoke" or "--password" or "--owner" or "--delete" or "--role" or "--set" or "--desec" or "--token" or "--email" or "--cert" or "--key") i++;
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
public sealed record CliNetworkDto(string Label, string Kind, string Origin, bool DefaultEnabled, bool TrustedSsl = false);

public sealed record CliStateDto(
    string ServerName, string? Email, string Version, string Origin, string ConnectLink,
    string? Pin, string? ShortPin, bool Lan, int Port, bool Unclaimed,
    IReadOnlyList<CliNetworkDto> DiscoveredNetworks,
    IReadOnlyList<CliNetworkDto> ActiveNetworks,
    IReadOnlyList<CliUserDto> Users, IReadOnlyList<CliDeviceDto> Devices,
    IReadOnlyList<CliBoardDto> Boards, IReadOnlyList<CliInviteDto> Invites,
    PublicDomainStatusDto? PublicDomain = null);

public sealed record CliClaimRequest(string Name, string Password, string ServerName, string Email, string? Networks = null);
public sealed record CliDesecDomainRequest(string Domain, string? Token, string? Email = null, string? Password = null, bool ForceRenew = false);
public sealed record CliCustomCertRequest(string Domain, string FullChainPath, string PrivateKeyPath);

public static class AdminCommands
{
    private const string By = "cli";

    public static readonly IReadOnlyList<CliCommand> All =
    [
        new("configure", "configure", "Interactive setup wizard and server management menu", Status),
        new("status", "status", "Address, certificate, users and boards", Status),
        new("setup", "setup", "Print a new setup link while the server has no owner", Setup),
        new("link", "link", "Print server addresses, web panel links and QR code", Link),
        new("domain", "domain [--desec DOMAIN --token TOKEN | --cert DOMAIN CHAIN KEY | --remove]", "Configure public domain, free *.dedyn.io DDNS & Let's Encrypt SSL", Domain),
        new("networks", "networks [--set URL1,URL2]", "List discovered and active networks (LAN, Tailscale, custom)", Networks),
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

        cli.MapGet("/json/state", async (AdminService admin, GrantService grants, ServerAddress address, ConnectLinks links, DomainManager domainManager, CancellationToken cancel) =>
        {
            var savedNets = await admin.SelectedNetworks(cancel);
            var discovered = address.DiscoverEndpoints()
                .Select(n => new CliNetworkDto(n.Label, n.Kind, n.Origin, n.DefaultEnabled, address.PinFor(n.Origin) is null))
                .ToList();
            var active = address.ActiveEndpoints(savedNets)
                .Select(n => new CliNetworkDto(n.Label, n.Kind, n.Origin, n.DefaultEnabled, address.PinFor(n.Origin) is null))
                .ToList();
            var primaryOrigin = active.Count > 0 ? active[0].Origin : address.DefaultOrigin;
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
            var publicDomain = await domainManager.GetStatusAsync(cancel, detectPublicIp: false);
            return Results.Ok(new CliStateDto(
                await admin.ServerName(cancel),
                await admin.ContactEmail(cancel),
                typeof(AdminCommands).Assembly.GetName().Version?.ToString(3) ?? "0.3.6",
                primaryOrigin,
                links.For(primaryOrigin, address.PinFor(primaryOrigin)),
                address.Pin,
                address.ShortPin,
                address.Mode == AddressMode.Lan,
                address.Port,
                await grants.Unclaimed(cancel),
                discovered,
                active,
                users,
                devices,
                boards,
                invites,
                publicDomain));
        });

        cli.MapGet("/json/domain/status", async (DomainManager domainManager, CancellationToken cancel) =>
            Results.Ok(await domainManager.GetStatusAsync(cancel, detectPublicIp: true)));

        cli.MapPost("/json/domain/desec", async (CliDesecDomainRequest req, DomainManager domainManager, CancellationToken cancel) =>
            Results.Ok(await domainManager.ConfigureDesecAsync(req.Domain, req.Token, req.Email, req.Password, req.ForceRenew, cancel)));

        cli.MapPost("/json/domain/custom-cert", async (CliCustomCertRequest req, DomainManager domainManager, CancellationToken cancel) =>
            Results.Ok(await domainManager.ConfigureCustomCertificateAsync(req.Domain, req.FullChainPath, req.PrivateKeyPath, cancel)));

        cli.MapPost("/json/domain/remove", async (DomainManager domainManager, CancellationToken cancel) =>
        {
            await domainManager.RemoveDomainAsync(cancel);
            return Results.Ok();
        });

        cli.MapPost("/json/claim", async (CliClaimRequest req, GrantService grants, AdminService admin, ServerAddress address, CancellationToken cancel) =>
        {
            if (AdminService.CleanEmail(req.Email) is null) return await Fail("Invalid email address.");
            if (req.ServerName.Trim() is not { Length: > 0 and <= AdminService.MaxServerName }) return await Fail("Server name must be 1 to 64 characters.");
            if (await grants.CreateClaim(cancel) is not { } token) return await Fail("This server already has an owner.");
            var result = await grants.Claim(token, req.Name, req.Password, cancel);
            if (result.Status != RedeemStatus.Ok) return await Fail($"Could not create owner account ({result.Status}).");
            await admin.Rename(req.ServerName, cancel);
            await admin.SetContactEmail(req.Email, cancel);
            if (!string.IsNullOrWhiteSpace(req.Networks))
            {
                await SaveNetworksAsync(req.Networks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), admin, address, cancel);
            }
            return await Text($"Server claimed by {result.User}.");
        });

        cli.MapPost("/json/networks", async (string[] origins, AdminService admin, ServerAddress address, CancellationToken cancel) =>
        {
            var saved = await SaveNetworksAsync(origins, admin, address, cancel);
            return saved.Count > 0
                ? await Text($"Active networks updated ({string.Join(", ", saved)}).")
                : await Fail("Specify at least one valid network address.");
        });

        cli.MapPost("/delete-invite", async (string[] args, AdminService admin, CancellationToken cancel) =>
            args.Length > 0 && await admin.DeleteInvite(args[0], cancel) ? await Text("Invite deleted.") : await Fail("No such invite."));
    }

    private static async Task<List<string>> SaveNetworksAsync(IEnumerable<string> rawOrigins, AdminService admin, ServerAddress address, CancellationToken cancel)
    {
        var scheme = address.Mode == AddressMode.Lan ? "https" : "http";
        var normalized = rawOrigins
            .Select(o => ServerAddress.NormalizeOrigin(o, address.Port, scheme))
            .Where(o => o is not null)
            .Select(o => o!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalized.Count > 0)
        {
            await admin.SetSelectedNetworks(string.Join(',', normalized), cancel);
            address.RefreshCertificate(normalized);
        }
        return normalized;
    }

    private static Task<IResult> Text(string text) => Task.FromResult(Results.Text(text.EndsWith('\n') ? text : text + "\n"));

    private static Task<IResult> Fail(string text) => Task.FromResult(Results.Text(text + "\n", statusCode: 400));

    private static string WithQr(string title, string url) => $"{title}\n\n  {url}\n\n{Qr.Terminal(url)}";

    private static async Task<IReadOnlyList<NetworkEndpoint>> ActiveNetworksOf(CliContext c)
    {
        var saved = await c.Get<AdminService>().SelectedNetworks(c.Cancel);
        return c.Get<ServerAddress>().ActiveEndpoints(saved);
    }

    private static async Task<IResult> Status(CliContext c)
    {
        var admin = c.Get<AdminService>();
        var address = c.Get<ServerAddress>();
        var nets = await ActiveNetworksOf(c);
        var counts = await admin.Counts(c.Cancel);
        var claimed = !await c.Get<GrantService>().Unclaimed(c.Cancel);
        var text = new StringBuilder()
            .AppendLine($"Name:         {await admin.ServerName(c.Cancel)}")
            .AppendLine($"Version:      {typeof(AdminCommands).Assembly.GetName().Version?.ToString(3)}");
        foreach (var net in nets)
            text.AppendLine($"{net.Label + ":",-14}{net.Origin}");
        text.AppendLine($"Mode:         {(address.Mode == AddressMode.Lan ? "HTTPS (all interfaces)" : "HTTP (local)")}");
        if (address.ShortPin is { } pin) text.AppendLine($"Certificate:  {pin}  ({address.Pin})");
        text.AppendLine($"Owner:        {(claimed ? "set" : "none yet, run: sudo penrill-server")}")
            .AppendLine($"Accounts:     {counts.Users}")
            .AppendLine($"Boards:       {counts.Boards}")
            .AppendLine($"Devices:      {counts.Devices}");
        return await Text(text.ToString());
    }

    private static async Task<IResult> Setup(CliContext c) =>
        await c.Get<SetupAnnouncer>().Message(c.Cancel) is { } message
            ? await Text(message)
            : await Text("This server already has an owner. To add people, run: sudo penrill-server invite");

    private static async Task<IResult> Link(CliContext c)
    {
        var address = c.Get<ServerAddress>();
        var links = c.Get<ConnectLinks>();
        var nets = await ActiveNetworksOf(c);
        var primary = nets[0];
        var sb = new StringBuilder();
        sb.AppendLine("Server Web Panel & Penrill Server Address (open in a browser or enter in Penrill › Sync › Your server):");
        sb.AppendLine();
        foreach (var net in nets)
            sb.AppendLine($"  • {net.Label,-24} {net.Origin}");
        sb.AppendLine();
        sb.AppendLine("Connect in Penrill (penrill.com/connect):");
        sb.AppendLine();
        foreach (var net in nets)
            sb.AppendLine($"  • {net.Label,-24} {links.For(net.Origin, address.PinFor(net.Origin))}");
        if (address.ShortPin is { } pin)
        {
            sb.AppendLine();
            sb.AppendLine($"TLS Certificate Fingerprint (LAN/Tailscale): {pin}  ({address.Pin})");
        }
        if (address.Certificate?.TrustedDomain is { } trustedDomain)
        {
            sb.AppendLine($"Public SSL Certificate ({trustedDomain}): {address.Certificate.TrustedIssuer ?? "Trusted CA"} (expires {address.Certificate.TrustedExpires:yyyy-MM-dd})");
        }
        sb.AppendLine();
        sb.AppendLine($"QR Code for {primary.Label} ({primary.Origin}):");
        sb.AppendLine();
        sb.Append(Qr.Terminal(primary.Origin));
        return await Text(sb.ToString());
    }

    private static async Task<IResult> Domain(CliContext c)
    {
        var dm = c.Get<DomainManager>();
        if (c.Flag("--remove"))
        {
            await dm.RemoveDomainAsync(c.Cancel);
            return await Text("Public domain configuration removed.");
        }
        if (c.Option("--desec") is { } desecDomain)
        {
            var token = c.Option("--token");
            var email = c.Option("--email");
            var password = c.Option("--password");
            var res = await dm.ConfigureDesecAsync(desecDomain, token, email, password, c.Flag("--renew"), c.Cancel);
            var sb = new StringBuilder();
            foreach (var step in res.Steps)
                sb.AppendLine($"  • {step}");
            sb.AppendLine(res.Message);
            return res.Ok ? await Text(sb.ToString()) : await Fail(sb.ToString());
        }
        if (c.Option("--cert") is { } certDomain)
        {
            var chainPath = c.Positional(0) ?? "";
            var keyPath = c.Option("--key") ?? c.Positional(1) ?? "";
            var res = await dm.ConfigureCustomCertificateAsync(certDomain, chainPath, keyPath, c.Cancel);
            return res.Ok ? await Text(res.Message) : await Fail(res.Message);
        }

        var status = await dm.GetStatusAsync(c.Cancel, detectPublicIp: true);
        var text = new StringBuilder();
        text.AppendLine($"Public Domain:   {status.Domain ?? "(not configured)"}");
        if (status.Origin is not null)
            text.AppendLine($"Public Address:  {status.Origin}");
        if (status.Provider is not null)
            text.AppendLine($"Provider:        {status.Provider}");
        text.AppendLine($"Public IPv4:     {status.PublicIp ?? "(unknown)"}");
        text.AppendLine($"Local LAN IP:    {status.LocalIp}:{status.Port}");
        text.AppendLine($"Trusted SSL:     {(status.HasTrustedCertificate ? $"Yes ({status.CertificateIssuer}, expires {status.CertificateExpires:yyyy-MM-dd})" : "No")}");
        return await Text(text.ToString());
    }

    private static async Task<IResult> Networks(CliContext c)
    {
        var admin = c.Get<AdminService>();
        var address = c.Get<ServerAddress>();
        if (c.Option("--set") is { } setCsv)
        {
            var saved = await SaveNetworksAsync(setCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), admin, address, c.Cancel);
            return saved.Count > 0
                ? await Text($"Active networks updated: {string.Join(", ", saved)}")
                : await Fail("No valid network addresses provided.");
        }
        var active = await ActiveNetworksOf(c);
        var activeSet = active.Select(a => a.Origin).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.AppendLine("Active networks:");
        foreach (var net in active)
            sb.AppendLine($"  [✓] {net.Label,-26} {net.Origin}");
        var others = address.DiscoverEndpoints().Where(d => !activeSet.Contains(d.Origin)).ToList();
        if (others.Count > 0)
        {
            sb.AppendLine("\nOther available networks on this machine:");
            foreach (var net in others)
                sb.AppendLine($"  [ ] {net.Label,-26} {net.Origin}");
        }
        return await Text(sb.ToString());
    }

    private static async Task<IResult> Invite(CliContext c)
    {
        var uses = int.TryParse(c.Option("--uses"), CultureInfo.InvariantCulture, out var n) ? n : 1;
        var days = double.TryParse(c.Option("--days"), CultureInfo.InvariantCulture, out var d) ? d : 7;
        var invite = await c.Get<AdminService>().Invite(By, c.Flag("--admin") ? Role.Admin : Role.Member, TimeSpan.FromDays(days), uses, c.Cancel);
        var nets = await ActiveNetworksOf(c);
        var primaryUrl = $"{nets[0].Origin}/invite/{invite.Token}";
        var sb = new StringBuilder();
        sb.AppendLine($"Invite link, {(uses == 1 ? "single use" : $"{uses} uses")}, valid for {days} days:");
        sb.AppendLine();
        if (nets.Count == 1)
        {
            sb.AppendLine($"  {primaryUrl}");
        }
        else
        {
            foreach (var net in nets)
                sb.AppendLine($"  • {net.Label,-24} {net.Origin}/invite/{invite.Token}");
        }
        sb.AppendLine();
        sb.Append(Qr.Terminal(primaryUrl));
        return await Text(sb.ToString());
    }

    private static async Task<IResult> Reset(CliContext c)
    {
        if (await c.Get<AdminService>().ResetLink(By, c.Positional(0), c.Cancel) is not { } reset) return await Fail("No such account.");
        var nets = await ActiveNetworksOf(c);
        var primaryUrl = $"{nets[0].Origin}/reset/{reset.Token}";
        var sb = new StringBuilder();
        sb.AppendLine($"Password reset link for {reset.Grant.Subject}, valid for one hour:");
        sb.AppendLine();
        if (nets.Count == 1)
        {
            sb.AppendLine($"  {primaryUrl}");
        }
        else
        {
            foreach (var net in nets)
                sb.AppendLine($"  • {net.Label,-24} {net.Origin}/reset/{reset.Token}");
        }
        sb.AppendLine();
        sb.Append(Qr.Terminal(primaryUrl));
        return await Text(sb.ToString());
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
