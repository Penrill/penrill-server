using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using Penrill.Core.Accounts;
using Penrill.Core.Admin;
using Penrill.Server.Hosting;

namespace Penrill.Server.Cli;

/// <summary>
/// Interactive terminal configurator and administration panel for Penrill Server.
/// Communicates with the running daemon over its Unix admin socket.
/// </summary>
public static class InteractiveConfigurator
{
    public static async Task<int> RunAsync(HttpClient http)
    {
        TerminalUi.EnsureCursorRestoredOnExit();
        var state = await FetchStateAsync(http);
        if (state is null)
            return await RunOfflineRecoveryMenuAsync(http);

        if (state.Unclaimed)
        {
            await RunFirstSetupWizardAsync(http, state);
            state = await FetchStateAsync(http);
            if (state is null) return 1;
        }

        while (true)
        {
            state = await FetchStateAsync(http);
            if (state is null) return 1;

            DrawStatusHeader(state);

            var options = new List<SelectOption<string>>();
            if (state.Unclaimed)
                options.Add(new("⚡ Finish First-Time Setup", "setup", "Create the owner account and claim this server"));

            var netSummary = string.Join(", ", state.ActiveNetworks.Select(n => n.Label));
            options.AddRange([
                new("🔗 Connect & Web Panel", "connect", $"Addresses & QR codes ({netSummary})"),
                new("👥 Users & Passwords", "users", $"{state.Users.Count} account(s) — create, reset password, role, delete"),
                new("✉️  Invites", "invites", $"{state.Invites.Count} active — create or revoke invite links"),
                new("📱 Linked Devices", "devices", $"{state.Devices.Count} device(s) — view or revoke access"),
                new("📋 Boards", "boards", $"{state.Boards.Count} board(s) — view, assign owner, or delete"),
                new("🌍 Networks & Settings", "settings", "Choose networks (LAN, Tailscale, custom), name, email & port"),
                new("💾 Backup Server", "backup", "Export all boards, images & accounts to a .zip archive"),
                new("🔄 Service & Uninstall", "service", "Restart, logs, factory reset, or uninstall Penrill Server"),
                new("🚪 Exit", "exit", "Close configurator")
            ]);

            var (selected, choice) = TerminalUi.Select("Penrill Server Control Center", options);
            if (!selected || choice == "exit")
            {
                Console.WriteLine();
                return 0;
            }

            switch (choice)
            {
                case "setup":
                    await RunFirstSetupWizardAsync(http, state);
                    break;
                case "connect":
                    await ShowConnectScreenAsync(state);
                    break;
                case "users":
                    await ManageUsersAsync(http);
                    break;
                case "invites":
                    await ManageInvitesAsync(http);
                    break;
                case "devices":
                    await ManageDevicesAsync(http);
                    break;
                case "boards":
                    await ManageBoardsAsync(http);
                    break;
                case "settings":
                    await ManageSettingsAsync(http, state);
                    break;
                case "backup":
                    await RunBackupInteractiveAsync(http);
                    break;
                case "service":
                    if (await ManageServiceAsync()) return 0;
                    break;
            }
        }
    }

    private static void DrawStatusHeader(CliStateDto state)
    {
        var owner = state.Users.FirstOrDefault(u => u.Role == "owner")?.Name;
        var ownerDisplay = state.Unclaimed
            ? $"{TerminalUi.Amber}Unclaimed (setup required){TerminalUi.Reset}"
            : $"{TerminalUi.Green}{owner ?? "set"}{TerminalUi.Reset}";
        var rows = new List<(string Key, string Value)>
        {
            ("Name:", state.ServerName)
        };
        if (state.ActiveNetworks.Count == 0)
        {
            rows.Add(("Address:", $"{TerminalUi.Cyan}{state.Origin}{TerminalUi.Reset}"));
        }
        else
        {
            foreach (var net in state.ActiveNetworks)
            {
                var shortLabel = net.Label.Length > 12 ? net.Label[..12] : net.Label;
                rows.Add((shortLabel + ":", $"{TerminalUi.Cyan}{net.Origin}{TerminalUi.Reset}"));
            }
        }
        if (state.ShortPin is { } pin)
            rows.Add(("Certificate:", pin));
        if (!string.IsNullOrEmpty(state.Email))
            rows.Add(("Email:", state.Email));
        rows.Add(("Owner:", ownerDisplay));
        rows.Add(("Resources:", $"{state.Users.Count} users · {state.Boards.Count} boards · {state.Devices.Count} devices"));

        TerminalUi.DrawBox(
            $"Penrill Server v{state.Version}",
            state.Lan ? "LAN · HTTPS" : "LOCAL · HTTP",
            rows);
    }

    private static Task ShowConnectScreenAsync(CliStateDto state)
    {
        var activeIndex = 0;
        var useAppLink = false;
        while (true)
        {
            var nets = state.ActiveNetworks.Count > 0
                ? state.ActiveNetworks
                : [new CliNetworkDto("Server", "lan", state.Origin, true)];
            var current = nets[Math.Clamp(activeIndex, 0, nets.Count - 1)];
            var connectBase = state.ConnectLink.Split('#')[0];
            string AppLinkOf(string origin) =>
                connectBase + "#server=" + Uri.EscapeDataString(origin) + (state.Pin is null ? "" : "&pin=" + Uri.EscapeDataString(state.Pin));

            var qrUrl = useAppLink ? AppLinkOf(current.Origin) : current.Origin;
            var qrTitle = useAppLink ? $"Penrill App Connect ({current.Label})" : $"Server Web Panel ({current.Label})";

            TerminalUi.Header("Connect Penrill & Web Panel", $"{nets.Count} active network(s)");
            Console.WriteLine();
            Console.WriteLine($"  {TerminalUi.Bold}Server Web Admin & Account Panel:{TerminalUi.Reset}");
            Console.WriteLine($"  {TerminalUi.Muted}(Open in browser to manage server, or enter in Penrill › Sync › Your server){TerminalUi.Reset}");
            Console.WriteLine();
            for (var i = 0; i < nets.Count; i++)
            {
                var net = nets[i];
                var marker = i == activeIndex && !useAppLink ? $"{TerminalUi.Green}●{TerminalUi.Reset}" : " ";
                Console.WriteLine($"  {marker} {TerminalUi.Bold}{net.Label,-26}{TerminalUi.Reset} {TerminalUi.Cyan}{net.Origin}{TerminalUi.Reset}");
            }
            Console.WriteLine();
            Console.WriteLine($"  {TerminalUi.Bold}Penrill Web App Connect Links (app.penrill.com):{TerminalUi.Reset}");
            Console.WriteLine();
            for (var i = 0; i < nets.Count; i++)
            {
                var net = nets[i];
                var marker = i == activeIndex && useAppLink ? $"{TerminalUi.Green}●{TerminalUi.Reset}" : " ";
                Console.WriteLine($"  {marker} {TerminalUi.Bold}{net.Label,-26}{TerminalUi.Reset} {TerminalUi.Cyan}{AppLinkOf(net.Origin)}{TerminalUi.Reset}");
            }
            if (state.ShortPin is { } pin)
            {
                Console.WriteLine();
                Console.WriteLine($"  {TerminalUi.Muted}TLS Certificate Fingerprint:{TerminalUi.Reset} {TerminalUi.Bold}{pin}{TerminalUi.Reset}  {TerminalUi.Muted}({state.Pin}){TerminalUi.Reset}");
            }
            Console.WriteLine();
            Console.WriteLine($"  {TerminalUi.Bold}QR Code — {qrTitle}:{TerminalUi.Reset}");
            Console.WriteLine();
            Console.Write(Qr.Terminal(qrUrl));
            Console.WriteLine();

            var switchOpts = new List<SelectOption<string>>();
            for (var i = 0; i < nets.Count; i++)
            {
                if (i != activeIndex || useAppLink)
                    switchOpts.Add(new($"🌐 Show Server Web Panel QR for {nets[i].Label}", $"panel:{i}", nets[i].Origin));
                if (i != activeIndex || !useAppLink)
                    switchOpts.Add(new($"📱 Show Penrill App Connect QR for {nets[i].Label}", $"app:{i}", "app.penrill.com/connect"));
            }
            switchOpts.Add(new("← Back to main menu", "back"));

            var (ok, pick) = TerminalUi.Select("Switch QR code or return", switchOpts);
            if (!ok || pick == "back") return Task.CompletedTask;
            var parts = pick.Split(':');
            useAppLink = parts[0] == "app";
            activeIndex = int.Parse(parts[1], CultureInfo.InvariantCulture);
        }
    }

    private static async Task RunFirstSetupWizardAsync(HttpClient http, CliStateDto state)
    {
        DrawStatusHeader(state);
        TerminalUi.Header("First-Time Server Setup", "This server does not have an owner account yet");
        Console.WriteLine();

        var (selected, mode) = TerminalUi.Select<string>(
            "How would you like to set up your Penrill Server?",
            [
                new("⚡ Configure right here in the terminal (recommended)", "terminal", "Choose networks, create owner account & password now"),
                new("🌐 Finish setup in a web browser", "browser", "Print a one-time setup link & QR code"),
                new("⏭️  Skip for now", "skip", "Go to the main management menu")
            ]);

        if (!selected || mode == "skip") return;

        if (mode == "browser")
        {
            Console.WriteLine();
            await RunCliVerbAsync(http, "setup", []);
            TerminalUi.Pause();
            return;
        }

        Console.WriteLine();
        var serverName = TerminalUi.Prompt(
            "Server display name",
            defaultValue: state.ServerName,
            validate: v => v.Length is > 0 and <= AdminService.MaxServerName ? null : "Enter 1 to 64 characters.");
        if (serverName is null) return;

        var chosenNetworks = PickNetworksInteractive(state);
        if (chosenNetworks is null) return;

        var username = TerminalUi.Prompt(
            "Owner username",
            defaultValue: "admin",
            validate: v => AccountService.Normalize(v) is not null ? null : "Use 3 to 32 lowercase letters, digits, '_' or '-'.");
        if (username is null) return;

        var password = TerminalUi.PromptPassword("Owner password", confirm: true);
        if (password is null) return;

        var email = TerminalUi.Prompt(
            "Administrator contact email",
            defaultValue: $"{AccountService.Normalize(username)}@local.lan",
            validate: v => AdminService.CleanEmail(v) is not null ? null : "Enter a valid email address (e.g. name@example.com).");
        if (email is null) return;

        var networksCsv = string.Join(',', chosenNetworks);
        var response = await http.PostAsJsonAsync("/cli/json/claim", new CliClaimRequest(username, password, serverName, email, networksCsv));
        var body = (await response.Content.ReadAsStringAsync()).Trim();
        Console.WriteLine();
        if (!response.IsSuccessStatusCode)
        {
            TerminalUi.Error(body);
            TerminalUi.Pause();
            return;
        }

        TerminalUi.Success($"Owner account '{AccountService.Normalize(username)}' created and server '{serverName}' is ready!");
        Console.WriteLine();
        var updatedState = await FetchStateAsync(http);
        if (updatedState is not null)
            await ShowConnectScreenAsync(updatedState);
        else
            await RunCliVerbAsync(http, "link", []);
    }

    private static List<string>? PickNetworksInteractive(CliStateDto state)
    {
        var activeSet = state.ActiveNetworks.Select(a => a.Origin).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allCandidates = new List<CliNetworkDto>(state.DiscoveredNetworks);
        foreach (var active in state.ActiveNetworks)
        {
            if (allCandidates.All(c => !string.Equals(c.Origin, active.Origin, StringComparison.OrdinalIgnoreCase)))
                allCandidates.Add(active);
        }

        var items = allCandidates
            .Select(n => (
                new SelectOption<string>(n.Label, n.Origin, n.Origin),
                activeSet.Count > 0 ? activeSet.Contains(n.Origin) : n.DefaultEnabled))
            .Append((
                new SelectOption<string>("+ Add custom domain, Tailscale DNS or IP...", "+custom", "Enter any extra address or hostname"),
                false))
            .ToList();

        var (ok, picked) = TerminalUi.MultiSelect(
            "Which networks should Penrill Server use?",
            items,
            hint: "↑/↓ move · Space toggle · Enter confirm");
        if (!ok) return null;

        var result = picked.Where(p => p != "+custom").ToList();
        if (picked.Contains("+custom"))
        {
            var scheme = state.Lan ? "https" : "http";
            var customRaw = TerminalUi.Prompt(
                "Custom hostname, IP or URL (comma-separated for multiple)",
                placeholder: $"e.g. my-pi.tail123.ts.net or 100.92.x.x:{state.Port}",
                validate: v =>
                {
                    var parts = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    return parts.Length > 0 && parts.All(p => ServerAddress.NormalizeOrigin(p, state.Port, scheme) is not null)
                        ? null
                        : "Enter a valid hostname, IP address or URL.";
                });
            if (customRaw is null) return null;
            foreach (var part in customRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ServerAddress.NormalizeOrigin(part, state.Port, scheme) is { } norm &&
                    !result.Contains(norm, StringComparer.OrdinalIgnoreCase))
                    result.Add(norm);
            }
        }

        if (result.Count == 0 && allCandidates.Count > 0)
            result.Add(allCandidates[0].Origin);

        if (result.Count > 1)
        {
            var primaryOpts = result
                .Select(origin =>
                {
                    var label = allCandidates.FirstOrDefault(c => string.Equals(c.Origin, origin, StringComparison.OrdinalIgnoreCase))?.Label ?? "Custom";
                    return new SelectOption<string>($"{label} ({origin})", origin, "Shown first & used for default QR code");
                })
                .ToList();
            var (pOk, primary) = TerminalUi.Select("Which address should be the primary default?", primaryOpts);
            if (pOk && !string.IsNullOrEmpty(primary))
            {
                result.RemoveAll(r => string.Equals(r, primary, StringComparison.OrdinalIgnoreCase));
                result.Insert(0, primary);
            }
        }

        return result;
    }

    private static async Task ManageUsersAsync(HttpClient http)
    {
        while (true)
        {
            var state = await FetchStateAsync(http);
            if (state is null) return;

            TerminalUi.Header("Users & Passwords", $"{state.Users.Count} registered account(s)");
            var options = new List<SelectOption<string>>
            {
                new("➕ Create new user account", "+create", "Add an account with username & password directly")
            };
            foreach (var u in state.Users)
            {
                var icon = u.Role switch { "owner" => "👑", "admin" => "🛡️ ", _ => "👤" };
                var status = u.Disabled ? " · disabled" : "";
                options.Add(new($"{icon} {u.Name}", u.Name, $"{u.Role}{status}"));
            }
            options.Add(new("← Back to main menu", "+back"));

            var (selected, pick) = TerminalUi.Select("Select a user or action", options);
            if (!selected || pick == "+back") return;

            if (pick == "+create")
            {
                await CreateUserInteractiveAsync(http);
                continue;
            }

            var target = state.Users.FirstOrDefault(u => u.Name == pick);
            if (target is not null)
                await ManageSingleUserAsync(http, target);
        }
    }

    private static async Task CreateUserInteractiveAsync(HttpClient http)
    {
        Console.WriteLine();
        var username = TerminalUi.Prompt(
            "New username",
            placeholder: "3-32 chars: a-z, 0-9, _, -",
            validate: v => AccountService.Normalize(v) is not null ? null : "Use 3 to 32 letters, digits, '_' or '-'.");
        if (username is null) return;

        var password = TerminalUi.PromptPassword($"Password for '{AccountService.Normalize(username)}'", confirm: true);
        if (password is null) return;

        var (roleSelected, isAdmin) = TerminalUi.Select<bool>(
            "Account role",
            [
                new("👤 Member", false, "Can create, sync and share boards"),
                new("🛡️  Administrator", true, "Can also manage users, invites and server settings")
            ]);
        if (!roleSelected) return;

        var args = isAdmin
            ? new[] { username, "--admin", "--password", password }
            : new[] { username, "--password", password };
        Console.WriteLine();
        await RunCliVerbAsync(http, "create-user", args, useIcons: true);
        TerminalUi.Pause();
    }

    private static async Task ManageSingleUserAsync(HttpClient http, CliUserDto user)
    {
        var isOwner = user.Role == "owner";
        var actions = new List<SelectOption<string>>
        {
            new("🔑 Set a new password right now", "set-password", "Change password immediately in terminal"),
            new("🔗 Generate 1-hour password reset link & QR code", "reset-link", "Prints reset links for all enabled networks")
        };

        if (!isOwner)
        {
            var nextRole = user.Role == "admin" ? "member" : "admin";
            actions.Add(new($"🛡️  Change role to {nextRole}", $"role:{nextRole}", $"Current role: {user.Role}"));
            actions.Add(user.Disabled
                ? new("✅ Enable account", "enable", "Allow this user to sign in again")
                : new("🚫 Disable account", "disable", "Block sign-in and revoke active sessions"));
            actions.Add(new("🗑️  Delete account", "delete", "Permanently remove account and revoke all sessions"));
        }
        actions.Add(new("← Back to users", "back"));

        var (selected, action) = TerminalUi.Select($"Manage user '{user.Name}' ({user.Role})", actions);
        if (!selected || action == "back") return;

        switch (action)
        {
            case "set-password":
                var newPass = TerminalUi.PromptPassword($"New password for '{user.Name}'", confirm: true);
                if (newPass is null) return;
                Console.WriteLine();
                await RunCliVerbAsync(http, "set-password", [user.Name, "--password", newPass], useIcons: true);
                TerminalUi.Pause();
                break;

            case "reset-link":
                Console.WriteLine();
                await RunCliVerbAsync(http, "reset-password", [user.Name]);
                TerminalUi.Pause();
                break;

            case "role:admin" or "role:member":
                var targetRole = action.Split(':')[1];
                Console.WriteLine();
                await RunCliVerbAsync(http, "role", [user.Name, targetRole], useIcons: true);
                TerminalUi.Pause();
                break;

            case "enable" or "disable":
                Console.WriteLine();
                await RunCliVerbAsync(http, action, [user.Name], useIcons: true);
                TerminalUi.Pause();
                break;

            case "delete":
                if (!TerminalUi.Confirm(
                    $"Delete account '{user.Name}' permanently?",
                    yesLabel: $"Yes, delete '{user.Name}'",
                    noLabel: "No, keep account"))
                    return;
                Console.WriteLine();
                await RunCliVerbAsync(http, "delete-user", [user.Name], useIcons: true);
                TerminalUi.Pause();
                break;
        }
    }

    private static async Task ManageInvitesAsync(HttpClient http)
    {
        while (true)
        {
            var state = await FetchStateAsync(http);
            if (state is null) return;

            TerminalUi.Header("Invite Links", $"{state.Invites.Count} active invite(s)");
            var options = new List<SelectOption<string>>
            {
                new("✉️  Create single-use invite (1 person, 7 days)", "quick-1", "Prints invite link for all active networks + QR code"),
                new("🏫 Create classroom / team invite (25 uses, 7 days)", "quick-25", "Multi-use member invite link & QR code"),
                new("⚙️  Create custom invite...", "custom", "Choose role (Member/Admin), uses and expiration days")
            };

            foreach (var inv in state.Invites)
            {
                var exp = inv.Expires?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "never";
                options.Add(new($"🗑️  Revoke invite #{inv.Id}", $"del:{inv.Id}", $"{inv.Role} · {inv.UsesLeft} use(s) left · expires {exp}"));
            }
            options.Add(new("← Back to main menu", "back"));

            var (selected, choice) = TerminalUi.Select("Manage invites", options);
            if (!selected || choice == "back") return;

            if (choice == "quick-1")
            {
                Console.WriteLine();
                await RunCliVerbAsync(http, "invite", []);
                TerminalUi.Pause();
            }
            else if (choice == "quick-25")
            {
                Console.WriteLine();
                await RunCliVerbAsync(http, "invite", ["--uses", "25"]);
                TerminalUi.Pause();
            }
            else if (choice == "custom")
            {
                var (roleOk, isAdmin) = TerminalUi.Select<bool>(
                    "Role for invited account(s)",
                    [
                        new("👤 Member", false, "Standard board user"),
                        new("🛡️  Administrator", true, "Can manage users and server")
                    ]);
                if (!roleOk) continue;

                var uses = TerminalUi.Prompt(
                    "Maximum number of uses",
                    defaultValue: "1",
                    validate: v => int.TryParse(v, out var n) && n is >= 1 and <= 1000 ? null : "Enter a number between 1 and 1000.");
                if (uses is null) continue;

                var days = TerminalUi.Prompt(
                    "Valid for how many days",
                    defaultValue: "7",
                    validate: v => double.TryParse(v, CultureInfo.InvariantCulture, out var d) && d is >= 1 and <= 90 ? null : "Enter between 1 and 90 days.");
                if (days is null) continue;

                var args = new List<string> { "--uses", uses, "--days", days };
                if (isAdmin) args.Add("--admin");
                Console.WriteLine();
                await RunCliVerbAsync(http, "invite", [.. args]);
                TerminalUi.Pause();
            }
            else if (choice.StartsWith("del:", StringComparison.Ordinal))
            {
                var id = choice["del:".Length..];
                if (TerminalUi.Confirm($"Revoke invite #{id}?", yesLabel: "Yes, revoke invite"))
                {
                    Console.WriteLine();
                    await RunCliVerbAsync(http, "delete-invite", [id], useIcons: true);
                }
            }
        }
    }

    private static async Task ManageDevicesAsync(HttpClient http)
    {
        while (true)
        {
            var state = await FetchStateAsync(http);
            if (state is null) return;

            TerminalUi.Header("Linked Devices", $"{state.Devices.Count} linked editor device(s)");
            if (state.Devices.Count == 0)
            {
                TerminalUi.Info("No devices are linked yet.");
                TerminalUi.Pause();
                return;
            }

            var options = state.Devices
                .Select(d =>
                {
                    var last = d.LastUsed?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "never";
                    return new SelectOption<string>($"📱 {d.Name} ({d.User})", d.Id, $"id: {d.Id} · last used: {last}");
                })
                .Append(new SelectOption<string>("← Back to main menu", "+back"))
                .ToList();

            var (selected, deviceId) = TerminalUi.Select("Select a device to revoke access", options);
            if (!selected || deviceId == "+back") return;

            var device = state.Devices.First(d => d.Id == deviceId);
            if (TerminalUi.Confirm(
                $"Revoke device '{device.Name}' belonging to '{device.User}'?",
                yesLabel: "Yes, revoke device access"))
            {
                Console.WriteLine();
                await RunCliVerbAsync(http, "devices", ["--revoke", deviceId], useIcons: true);
                TerminalUi.Pause();
            }
        }
    }

    private static async Task ManageBoardsAsync(HttpClient http)
    {
        while (true)
        {
            var state = await FetchStateAsync(http);
            if (state is null) return;

            TerminalUi.Header("Boards", $"{state.Boards.Count} board(s) stored on this server");
            if (state.Boards.Count == 0)
            {
                TerminalUi.Info("No boards have been synced to this server yet.");
                TerminalUi.Pause();
                return;
            }

            var options = state.Boards
                .Select(b =>
                {
                    var owner = string.IsNullOrEmpty(b.Owner) ? "unassigned" : b.Owner;
                    var title = string.IsNullOrWhiteSpace(b.Title) ? "(Untitled)" : b.Title;
                    return new SelectOption<string>($"📋 {title}", b.Id, $"owner: {owner} · updated {b.Updated:yyyy-MM-dd} · {b.Id[..Math.Min(8, b.Id.Length)]}");
                })
                .Append(new SelectOption<string>("← Back to main menu", "+back"))
                .ToList();

            var (selected, boardId) = TerminalUi.Select("Select a board to manage", options);
            if (!selected || boardId == "+back") return;

            var board = state.Boards.First(b => b.Id == boardId);
            var (actOk, action) = TerminalUi.Select<string>(
                $"Board '{board.Title}' ({board.Id})",
                [
                    new("👤 Assign or change owner", "owner", $"Current owner: {(string.IsNullOrEmpty(board.Owner) ? "unassigned" : board.Owner)}"),
                    new("🗑️  Delete board permanently", "delete", "Removes board, history and assets for all connected devices"),
                    new("← Back to boards", "back")
                ]);
            if (!actOk || action == "back") continue;

            if (action == "owner")
            {
                if (state.Users.Count == 0)
                {
                    TerminalUi.Warn("Create a user account first before assigning a board owner.");
                    TerminalUi.Pause();
                    continue;
                }
                var userOpts = state.Users
                    .Select(u => new SelectOption<string>(u.Name, u.Name, u.Role))
                    .Append(new SelectOption<string>("← Cancel", "+cancel"))
                    .ToList();
                var (uOk, newOwner) = TerminalUi.Select("Select new owner for this board", userOpts);
                if (!uOk || newOwner == "+cancel") continue;
                Console.WriteLine();
                await RunCliVerbAsync(http, "boards", ["--owner", board.Id, newOwner], useIcons: true);
                TerminalUi.Pause();
            }
            else if (action == "delete")
            {
                if (TerminalUi.Confirm(
                    $"Permanently delete board '{board.Title}' ({board.Id})?",
                    yesLabel: "Yes, delete board permanently"))
                {
                    Console.WriteLine();
                    await RunCliVerbAsync(http, "boards", ["--delete", board.Id], useIcons: true);
                    TerminalUi.Pause();
                }
            }
        }
    }

    private static async Task ManageSettingsAsync(HttpClient http, CliStateDto state)
    {
        while (true)
        {
            var fresh = await FetchStateAsync(http) ?? state;
            var activeLabels = string.Join(", ", fresh.ActiveNetworks.Select(n => $"{n.Label} ({n.Origin})"));
            TerminalUi.Header("Networks & Server Settings");
            var (selected, choice) = TerminalUi.Select<string>(
                "Choose a setting to configure",
                [
                    new("🌍 Select Active Networks (LAN, Tailscale, mDNS, Custom)", "networks", $"Active: {activeLabels}"),
                    new("✏️  Server display name", "rename", $"Current: {fresh.ServerName}"),
                    new("📧 Administrator contact email", "email", $"Current: {fresh.Email ?? "(not set)"}"),
                    new("🔌 Listening TCP port, HTTPS mode & registration policy", "daemon-env", $"Current: port {fresh.Port} ({(fresh.Lan ? "LAN HTTPS" : "Local HTTP")})"),
                    new("← Back to main menu", "back")
                ]);

            if (!selected || choice == "back") return;

            if (choice == "networks")
            {
                Console.WriteLine();
                var picked = PickNetworksInteractive(fresh);
                if (picked is null || picked.Count == 0) continue;
                var resp = await http.PostAsJsonAsync("/cli/json/networks", picked.ToArray());
                var msg = (await resp.Content.ReadAsStringAsync()).Trim();
                Console.WriteLine();
                if (resp.IsSuccessStatusCode) TerminalUi.Success(msg);
                else TerminalUi.Error(msg);
                TerminalUi.Pause();
            }
            else if (choice == "rename")
            {
                var name = TerminalUi.Prompt(
                    "New server display name",
                    defaultValue: fresh.ServerName,
                    validate: v => v.Length is > 0 and <= AdminService.MaxServerName ? null : "Enter 1 to 64 characters.");
                if (name is null) continue;
                Console.WriteLine();
                await RunCliVerbAsync(http, "rename", [name], useIcons: true);
                TerminalUi.Pause();
            }
            else if (choice == "email")
            {
                var email = TerminalUi.Prompt(
                    "Administrator contact email",
                    defaultValue: fresh.Email ?? "",
                    validate: v => AdminService.CleanEmail(v) is not null ? null : "Enter a valid email address.");
                if (email is null) continue;
                Console.WriteLine();
                await RunCliVerbAsync(http, "email", [email], useIcons: true);
                TerminalUi.Pause();
            }
            else if (choice == "daemon-env")
            {
                await ConfigureServiceEnvironmentAsync(fresh);
                return;
            }
        }
    }

    private static async Task ConfigureServiceEnvironmentAsync(CliStateDto state)
    {
        Console.WriteLine();
        var (modeOk, addressMode) = TerminalUi.Select<string>(
            "Network listening mode",
            [
                new("🔒 HTTPS on all interfaces — LAN, Tailscale & VPN (recommended)", "lan", "Self-signed pinned TLS certificate on every network interface"),
                new("🏠 Local HTTP on 127.0.0.1 only", "local", "Use behind a local reverse proxy (Caddy, Nginx, Cloudflare Tunnel)")
            ],
            initialIndex: state.Lan ? 0 : 1);
        if (!modeOk) return;

        var port = TerminalUi.Prompt(
            "Listening TCP port",
            defaultValue: state.Port.ToString(CultureInfo.InvariantCulture),
            validate: v => int.TryParse(v, out var p) && p is >= 1 and <= 65535 ? null : "Enter a port between 1 and 65535.");
        if (port is null) return;

        var (regOk, openReg) = TerminalUi.Select<string>(
            "Account registration policy",
            [
                new("🔒 Invite-only (recommended)", "false", "Only people with an invite link can create an account"),
                new("🌍 Open registration", "true", "Anyone who can reach the server can register an account")
            ]);
        if (!regOk) return;

        var origins = TerminalUi.Prompt(
            "Extra web editor CORS origins (optional, comma-separated)",
            defaultValue: "",
            placeholder: "leave empty for default app.penrill.com");
        if (origins is null) return;

        Console.WriteLine();
        if (Environment.GetEnvironmentVariable("SNAP") is not null)
        {
            RunProcess("snap", ["set", "penrill-server", $"address-mode={addressMode}", $"port={port}", $"open-registration={openReg}", $"editor-origins={origins}"]);
            TerminalUi.Success("Snap configuration updated and service restarted.");
        }
        else if (Directory.Exists("/run/systemd/system"))
        {
            try
            {
                const string overrideDir = "/etc/systemd/system/penrill-server.service.d";
                Directory.CreateDirectory(overrideDir);
                var lines = new List<string>
                {
                    "[Service]",
                    $"Environment=\"PENRILL_ADDRESS_MODE={addressMode}\"",
                    $"Environment=\"PENRILL_PORT={port}\"",
                    $"Environment=\"PENRILL_OPEN_REGISTRATION={openReg}\""
                };
                if (!string.IsNullOrWhiteSpace(origins))
                    lines.Add($"Environment=\"PENRILL_EDITOR_ORIGINS={origins}\"");
                await File.WriteAllLinesAsync(Path.Combine(overrideDir, "override.conf"), lines);
                RunProcess("systemctl", ["daemon-reload"]);
                RunProcess("systemctl", ["restart", "penrill-server"]);
                TerminalUi.Success("Systemd configuration saved to /etc/systemd/system/penrill-server.service.d/override.conf and service restarted.");
            }
            catch (Exception ex)
            {
                TerminalUi.Error($"Could not write systemd override (run with sudo): {ex.Message}");
            }
        }
        else
        {
            TerminalUi.Warn("Not running under systemd or Snap. Set PENRILL_ADDRESS_MODE, PENRILL_PORT and PENRILL_OPEN_REGISTRATION in your environment.");
        }
        TerminalUi.Pause();
    }

    private static async Task RunBackupInteractiveAsync(HttpClient http)
    {
        Console.WriteLine();
        var defaultFile = $"penrill-backup-{DateTime.Now:yyyy-MM-dd}.zip";
        var target = TerminalUi.Prompt("Backup output file path", defaultValue: defaultFile);
        if (string.IsNullOrWhiteSpace(target)) return;

        var response = await http.PostAsJsonAsync("/cli/backup", new[] { target });
        if (!response.IsSuccessStatusCode)
        {
            TerminalUi.Error("Backup failed.");
            TerminalUi.Pause();
            return;
        }
        await using var file = File.Create(target);
        await response.Content.CopyToAsync(file);
        Console.WriteLine();
        TerminalUi.Success($"Backup archive written to {Path.GetFullPath(target)}");
        TerminalUi.Pause();
    }

    private static async Task<bool> ManageServiceAsync()
    {
        var isSnap = Environment.GetEnvironmentVariable("SNAP") is not null;
        TerminalUi.Header("Service Control & Uninstall");
        var (selected, action) = TerminalUi.Select<string>(
            "Select a service action",
            [
                new("🔄 Restart Penrill Server", "restart", isSnap ? "sudo snap restart penrill-server" : "sudo systemctl restart penrill-server"),
                new("📜 View recent service logs", "logs", "Show the last 30 log entries"),
                new("🔥 Factory Reset (wipe all data & start over)", "reset", "Deletes all accounts & boards and restarts fresh"),
                new("🗑️  Uninstall Penrill Server...", "uninstall", "Remove Penrill Server from this machine"),
                new("← Back to main menu", "back")
            ]);

        if (!selected || action == "back") return false;

        if (action == "restart")
        {
            Console.WriteLine();
            if (isSnap) RunProcess("snap", ["restart", "penrill-server"]);
            else RunProcess("systemctl", ["restart", "penrill-server"]);
            TerminalUi.Success("Penrill Server restarted.");
            await Task.Delay(800);
            TerminalUi.Pause();
            return false;
        }

        if (action == "logs")
        {
            Console.WriteLine();
            if (isSnap) RunProcess("snap", ["logs", "-n", "30", "penrill-server"]);
            else RunProcess("journalctl", ["-u", "penrill-server", "-n", "30", "--no-pager"]);
            TerminalUi.Pause();
            return false;
        }

        if (action == "reset")
        {
            await RunFactoryResetAsync(skipConfirm: false);
            return false;
        }

        if (action == "uninstall")
            return RunUninstallInteractive();

        return false;
    }

    private static async Task<int> RunOfflineRecoveryMenuAsync(HttpClient http)
    {
        var isSnap = Environment.GetEnvironmentVariable("SNAP") is not null;
        while (true)
        {
            TerminalUi.Header("Penrill Server — Service Recovery", "The background service is not responding");
            var (selected, action) = TerminalUi.Select<string>(
                "What would you like to do?",
                [
                    new("🔄 Start / Restart Penrill Server", "restart", isSnap ? "sudo snap restart penrill-server" : "sudo systemctl restart penrill-server"),
                    new("📜 View service logs (see why it stopped)", "logs", isSnap ? "snap logs -n 40 penrill-server" : "journalctl -u penrill-server -n 40"),
                    new("🔥 Factory Reset (wipe data & start clean)", "reset", "Clears all boards/accounts and starts a fresh server"),
                    new("🗑️  Uninstall Penrill Server", "uninstall", "Remove Penrill Server from this machine"),
                    new("🚪 Exit", "exit")
                ]);
            if (!selected || action == "exit") return 1;

            if (action == "restart")
            {
                Console.WriteLine();
                if (isSnap) RunProcess("snap", ["restart", "penrill-server"]);
                else RunProcess("systemctl", ["restart", "penrill-server"]);
                await WaitForSocketAsync();
                if (await FetchStateAsync(http) is not null)
                    return await RunAsync(http);
                TerminalUi.Warn("Service still did not come up. Check the logs below.");
            }
            else if (action == "logs")
            {
                Console.WriteLine();
                if (isSnap) RunProcess("snap", ["logs", "-n", "40", "penrill-server"]);
                else RunProcess("journalctl", ["-u", "penrill-server", "-n", "40", "--no-pager"]);
                TerminalUi.Pause();
            }
            else if (action == "reset")
            {
                if (await RunFactoryResetAsync(skipConfirm: false))
                {
                    if (await FetchStateAsync(http) is not null)
                        return await RunAsync(http);
                }
            }
            else if (action == "uninstall")
            {
                if (RunUninstallInteractive()) return 0;
            }
        }
    }

    public static async Task<bool> RunFactoryResetAsync(bool skipConfirm = false)
    {
        var isSnap = Environment.GetEnvironmentVariable("SNAP") is not null;
        var dataDir = isSnap ? "/var/snap/penrill-server/common" : "/var/lib/penrill-server";

        if (!skipConfirm)
        {
            Console.WriteLine();
            if (!TerminalUi.Confirm(
                $"Factory reset Penrill Server and DELETE ALL BOARDS & ACCOUNTS in {dataDir}?",
                yesLabel: "Yes, wipe everything and start fresh",
                noLabel: "No, cancel"))
                return false;
        }

        Console.WriteLine();
        TerminalUi.Info("Stopping Penrill Server...");
        if (isSnap) RunProcess("snap", ["stop", "penrill-server"]);
        else RunProcess("systemctl", ["stop", "penrill-server"]);

        try
        {
            if (Directory.Exists(dataDir))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(dataDir))
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                    else File.Delete(entry);
                }
            }
        }
        catch (Exception ex)
        {
            TerminalUi.Error($"Could not clear {dataDir}: {ex.Message}");
            return false;
        }

        TerminalUi.Info("Starting fresh Penrill Server...");
        if (isSnap) RunProcess("snap", ["start", "penrill-server"]);
        else RunProcess("systemctl", ["start", "penrill-server"]);

        await WaitForSocketAsync();
        TerminalUi.Success("Factory reset complete! Server is clean and ready for setup.");
        return true;
    }

    private static async Task WaitForSocketAsync()
    {
        var socket = Environment.GetEnvironmentVariable("SNAP_COMMON") is { Length: > 0 } sc
            ? Path.Combine(sc, "admin.sock")
            : CommandLine.DefaultSocket;
        for (var i = 0; i < 30 && !File.Exists(socket); i++)
            await Task.Delay(500);
    }

    public static bool RunUninstallInteractive(bool? forcePurge = null, bool skipConfirm = false)
    {
        var isSnap = Environment.GetEnvironmentVariable("SNAP") is not null;
        var dataDir = isSnap ? "/var/snap/penrill-server/common" : "/var/lib/penrill-server";

        bool purge;
        if (forcePurge is { } p)
        {
            purge = p;
        }
        else
        {
            Console.WriteLine();
            var (selected, mode) = TerminalUi.Select<string>(
                "How would you like to uninstall Penrill Server?",
                [
                    new("← Cancel (keep Penrill Server installed)", "cancel"),
                    new("📦 Uninstall package only (keep boards & accounts)", "keep", $"Preserves {dataDir} for future reinstalls"),
                    new("🔥 Uninstall & purge all data", "purge", $"Permanently deletes package AND all boards/accounts in {dataDir}")
                ]);
            if (!selected || mode == "cancel") return false;
            purge = mode == "purge";
        }

        if (!skipConfirm)
        {
            var question = purge
                ? $"Are you sure you want to UNINSTALL Penrill Server and DELETE ALL BOARDS in {dataDir}?"
                : "Are you sure you want to uninstall Penrill Server (keeping board data)?";
            if (!TerminalUi.Confirm(question, yesLabel: purge ? "Yes, uninstall and delete all data" : "Yes, uninstall Penrill Server"))
                return false;
        }

        Console.WriteLine();
        if (isSnap)
        {
            TerminalUi.Info("Removing snap package 'penrill-server'...");
            var args = purge ? new[] { "remove", "--purge", "penrill-server" } : new[] { "remove", "penrill-server" };
            RunProcess("snap", args);
        }
        else
        {
            TerminalUi.Info("Uninstalling deb package 'penrill-server'...");
            var action = purge ? "purge" : "remove";
            RunProcess("apt-get", [action, "-y", "penrill-server"]);
            if (purge)
            {
                try
                {
                    if (Directory.Exists("/var/lib/penrill-server"))
                        Directory.Delete("/var/lib/penrill-server", recursive: true);
                    if (Directory.Exists("/etc/systemd/system/penrill-server.service.d"))
                        Directory.Delete("/etc/systemd/system/penrill-server.service.d", recursive: true);
                    if (File.Exists("/etc/apt/sources.list.d/penrill.list"))
                        File.Delete("/etc/apt/sources.list.d/penrill.list");
                }
                catch (Exception ex)
                {
                    TerminalUi.Warn($"Could not clean up some data files: {ex.Message}");
                }
            }
        }

        TerminalUi.Success(purge
            ? "Penrill Server and all its data have been completely removed."
            : $"Penrill Server has been uninstalled. Your boards remain in {dataDir}.");
        return true;
    }

    private static async Task<CliStateDto?> FetchStateAsync(HttpClient http)
    {
        try
        {
            return await http.GetFromJsonAsync<CliStateDto>("/cli/json/state");
        }
        catch (Exception error)
        {
            TerminalUi.Error($"Could not reach Penrill Server: {error.Message}");
            return null;
        }
    }

    private static async Task RunCliVerbAsync(HttpClient http, string verb, string[] args, bool useIcons = false)
    {
        var response = await http.PostAsJsonAsync("/cli/" + verb, args);
        var text = await response.Content.ReadAsStringAsync();
        if (useIcons)
        {
            if (response.IsSuccessStatusCode) TerminalUi.Success(text.Trim());
            else TerminalUi.Error(text.Trim());
        }
        else
        {
            (response.IsSuccessStatusCode ? Console.Out : Console.Error).Write(text);
        }
    }

    private static void RunProcess(string fileName, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch (Exception ex)
        {
            TerminalUi.Error($"Failed to run '{fileName}': {ex.Message}");
        }
    }
}
