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
            var domainSummary = state.PublicDomain?.Domain is { Length: > 0 } activeDom
                ? $"{state.PublicDomain.Origin} ({(state.PublicDomain.HasTrustedCertificate ? "Trusted Let's Encrypt SSL" : "Configured")})"
                : "Free *.duckdns.org domain + auto Let's Encrypt SSL for internet & guest sharing";
            options.AddRange([
                new("🔗 Connect & Web Panel", "connect", $"Addresses & QR codes ({netSummary})"),
                new("🌐 Public Domain & SSL (Guest Sharing)", "domain", domainSummary),
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
                case "domain":
                    await ManagePublicDomainAsync(http, state);
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
        if (state.PublicDomain is { Domain: { Length: > 0 }, HasTrustedCertificate: true } pd)
            rows.Add(("Public SSL:", $"{TerminalUi.Green}{pd.CertificateIssuer ?? "Trusted CA"} (expires {pd.CertificateExpires:yyyy-MM-dd}){TerminalUi.Reset}"));
        if (state.ShortPin is { } pin)
            rows.Add(("LAN Cert Pin:", pin));
        if (!string.IsNullOrEmpty(state.Email))
            rows.Add(("Email:", state.Email));
        rows.Add(("Owner:", ownerDisplay));
        rows.Add(("Resources:", $"{state.Users.Count} users · {state.Boards.Count} boards · {state.Devices.Count} devices"));

        var modeBadge = state.PublicDomain is { HasTrustedCertificate: true }
            ? "PUBLIC SSL · HTTPS"
            : state.Lan ? "LAN · HTTPS" : "LOCAL · HTTP";

        TerminalUi.DrawBox(
            $"Penrill Server v{state.Version}",
            modeBadge,
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
            string AppLinkOf(CliNetworkDto net) =>
                connectBase + "#server=" + Uri.EscapeDataString(net.Origin) + (net.TrustedSsl || state.Pin is null ? "" : "&pin=" + Uri.EscapeDataString(state.Pin));

            var qrUrl = useAppLink ? AppLinkOf(current) : current.Origin;
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
                var sslBadge = net.TrustedSsl ? $" {TerminalUi.Green}[Trusted SSL]{TerminalUi.Reset}" : "";
                Console.WriteLine($"  {marker} {TerminalUi.Bold}{net.Label,-26}{TerminalUi.Reset} {TerminalUi.Cyan}{net.Origin}{TerminalUi.Reset}{sslBadge}");
            }
            Console.WriteLine();
            Console.WriteLine($"  {TerminalUi.Bold}Penrill Connect Links (penrill.com/connect):{TerminalUi.Reset}");
            Console.WriteLine();
            for (var i = 0; i < nets.Count; i++)
            {
                var net = nets[i];
                var marker = i == activeIndex && useAppLink ? $"{TerminalUi.Green}●{TerminalUi.Reset}" : " ";
                Console.WriteLine($"  {marker} {TerminalUi.Bold}{net.Label,-26}{TerminalUi.Reset} {TerminalUi.Cyan}{AppLinkOf(net)}{TerminalUi.Reset}");
            }
            if (state.ShortPin is { } pin)
            {
                Console.WriteLine();
                Console.WriteLine($"  {TerminalUi.Muted}TLS Certificate Fingerprint (LAN/Tailscale):{TerminalUi.Reset} {TerminalUi.Bold}{pin}{TerminalUi.Reset}  {TerminalUi.Muted}({state.Pin}){TerminalUi.Reset}");
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
                    switchOpts.Add(new($"📱 Show Penrill App Connect QR for {nets[i].Label}", $"app:{i}", "penrill.com/connect"));
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

        var (wantDomainOk, wantDomain) = TerminalUi.Select<string>(
            "Would you like to connect a public domain & Let's Encrypt SSL now (for internet & guest board sharing)?",
            [
                new("⏭️  Skip for now (use LAN / Tailscale first)", "skip", "You can always set up a public domain later from the main menu"),
                new("📡 Yes — Enable Tailscale Funnel (Best for Starlink / CGNAT)", "funnel", "Exposes https://<your-pi>.ts.net on port 443 to internet guests without port forwarding"),
                new("🦆 Yes — connect a free *.duckdns.org domain (DuckDNS + Auto Let's Encrypt SSL)", "duckdns", "1-click GitHub/Google sign-in on duckdns.org + auto browser-trusted SSL"),
                new("🌐 Yes — connect a domain on deSEC DNS (Auto Let's Encrypt SSL)", "desec", "For custom domains delegated to desec.io")
            ]);
        if (wantDomainOk && wantDomain == "funnel")
        {
            await RunTailscaleFunnelSetupWizardAsync(http, state);
        }
        else if (wantDomainOk && wantDomain == "duckdns")
        {
            await RunDuckDnsSetupWizardAsync(http, null, state);
        }
        else if (wantDomainOk && wantDomain == "desec")
        {
            await RunDesecSetupWizardAsync(http, null);
        }

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
                    new("🌐 Public Domain & SSL (deSEC *.dedyn.io / Custom)", "domain", fresh.PublicDomain?.Domain is { Length: > 0 } d ? $"Configured: {fresh.PublicDomain.Origin}" : "Not configured — local/VPN only"),
                    new("🌍 Select Active Networks (LAN, Tailscale, mDNS, Custom)", "networks", $"Active: {activeLabels}"),
                    new("✏️  Server display name", "rename", $"Current: {fresh.ServerName}"),
                    new("📧 Administrator contact email", "email", $"Current: {fresh.Email ?? "(not set)"}"),
                    new("🔌 Listening TCP port, HTTPS mode & registration policy", "daemon-env", $"Current: port {fresh.Port} ({(fresh.Lan ? "LAN HTTPS" : "Local HTTP")})"),
                    new("← Back to main menu", "back")
                ]);

            if (!selected || choice == "back") return;

            if (choice == "domain")
            {
                await ManagePublicDomainAsync(http, fresh);
            }
            else if (choice == "networks")
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

    private static async Task ManagePublicDomainAsync(HttpClient http, CliStateDto state)
    {
        while (true)
        {
            PublicDomainStatusDto? status = null;
            try
            {
                status = await http.GetFromJsonAsync<PublicDomainStatusDto>("/cli/json/domain/status");
            }
            catch
            {
                status = state.PublicDomain;
            }

            var hasDomain = !string.IsNullOrWhiteSpace(status?.Domain);
            var rows = new List<(string Key, string Value)>
            {
                ("Public Domain:", hasDomain
                    ? $"{TerminalUi.Cyan}{status!.Origin}{TerminalUi.Reset}"
                    : $"{TerminalUi.Amber}Not configured (LAN / VPN only){TerminalUi.Reset}"),
                ("Public IPv4:", status?.PublicIp ?? "detecting..."),
                ("Local Server:", $"{status?.LocalIp ?? "127.0.0.1"}:{status?.Port ?? state.Port}"),
                ("SSL Status:", status is { HasTrustedCertificate: true }
                    ? $"{TerminalUi.Green}Trusted ({status.CertificateIssuer ?? "Let's Encrypt"} · valid until {status.CertificateExpires:yyyy-MM-dd}){TerminalUi.Reset}"
                    : $"{TerminalUi.Muted}Self-signed certificate (requires trusted domain for browser guests){TerminalUi.Reset}")
            };

            TerminalUi.DrawBox(
                "Public Domain & Let's Encrypt SSL",
                hasDomain ? "INTERNET · HTTPS" : "LOCAL ONLY",
                rows);

            var options = new List<SelectOption<string>>
            {
                new("📡 Enable Tailscale Funnel (Bypass Starlink / CGNAT — Zero Port Forwarding)", "funnel",
                    "Public https://<host>.ts.net on port 443 with Let's Encrypt SSL (guests do NOT need Tailscale!)"),
                new("🦆 Connect free *.duckdns.org domain (DuckDNS + Auto Let's Encrypt SSL)", "duckdns",
                    "1-click login on duckdns.org + auto DDNS + real Let's Encrypt SSL (no port 80 needed!)"),
                new("🌐 Connect domain via deSEC DNS (Managed DNS + Auto Let's Encrypt SSL)", "desec",
                    "For custom domains delegated to desec.io or existing *.dedyn.io domains")
            };

            if (hasDomain && status?.Provider is "duckdns" or "desec")
            {
                options.Add(new("🔄 Force DDNS IP update & Let's Encrypt SSL renewal now", "refresh-ddns",
                    $"Re-sync {status.Domain} IP and verify SSL certificate"));
            }

            if (hasDomain)
            {
                options.Add(new("🩺 Test internet reachability & router port forwarding", "probe",
                    $"Check if {status!.Origin} is reachable from outside your network"));
            }

            options.Add(new("🔐 Use custom domain with your own SSL certificate files (.pem)", "custom-cert",
                "Import existing fullchain.pem + privkey.pem for any domain"));

            if (hasDomain)
            {
                options.Add(new("🗑️  Remove public domain configuration", "remove",
                    $"Disconnect {status!.Domain} and revert to LAN/Tailscale only"));
            }

            options.Add(new("← Back", "back"));

            var (selected, action) = TerminalUi.Select("Public Domain & Guest Board Sharing Setup", options);
            if (!selected || action == "back") return;

            if (action == "funnel")
            {
                await RunTailscaleFunnelSetupWizardAsync(http, state);
            }
            else if (action == "duckdns")
            {
                await RunDuckDnsSetupWizardAsync(http, status, state);
            }
            else if (action == "desec")
            {
                await RunDesecSetupWizardAsync(http, status);
            }
            else if (action == "refresh-ddns" && status?.Domain is { } existingDomain)
            {
                Console.WriteLine();
                TerminalUi.Info($"Refreshing DDNS and checking Let's Encrypt SSL for {existingDomain}...");
                var resp = status.Provider == "duckdns"
                    ? await http.PostAsJsonAsync("/cli/json/domain/duckdns", new CliDuckDnsDomainRequest(existingDomain, null, ForceRenew: false))
                    : await http.PostAsJsonAsync("/cli/json/domain/desec", new CliDesecDomainRequest(existingDomain, null, ForceRenew: false));
                var res = await resp.Content.ReadFromJsonAsync<DomainSetupResultDto>();
                PrintDomainSetupResult(res);
                TerminalUi.Pause();
            }
            else if (action == "probe" && status?.Origin is { } origin)
            {
                Console.WriteLine();
                TerminalUi.Info($"Testing external HTTPS reachability for {origin}/health ...");
                var ok = await DomainManager.ProbeExternalReachabilityAsync(origin, CancellationToken.None);
                Console.WriteLine();
                if (ok)
                {
                    TerminalUi.Success($"{origin} is reachable from the internet with a trusted SSL certificate! Guest board sharing is ready.");
                }
                else
                {
                    PrintRouterPortForwardingGuide(origin, status.PublicIp, status.LocalIp, status.Port);
                }
                TerminalUi.Pause();
            }
            else if (action == "custom-cert")
            {
                await RunCustomCertWizardAsync(http, status);
            }
            else if (action == "remove" && status?.Domain is { } domToRemove)
            {
                if (TerminalUi.Confirm($"Remove public domain '{domToRemove}' and its SSL certificate from Penrill Server?",
                    yesLabel: $"Yes, remove {domToRemove}"))
                {
                    if (status.Provider == "tailscale-funnel")
                    {
                        RunProcess("tailscale", ["funnel", "--https=443", "off"]);
                    }
                    await http.PostAsync("/cli/json/domain/remove", null);
                    Console.WriteLine();
                    TerminalUi.Success("Public domain configuration removed.");
                    TerminalUi.Pause();
                }
            }
        }
    }

    private static async Task RunTailscaleFunnelSetupWizardAsync(HttpClient http, CliStateDto state)
    {
        TerminalUi.Header(
            "Enable Tailscale Funnel (Bypass Starlink & CGNAT)",
            "Exposes your server at https://<your-pi>.ts.net with Let's Encrypt SSL — guests do NOT need Tailscale");
        Console.WriteLine();

        var tsDns = ServerAddress.TryGetTailscaleDnsName();
        if (string.IsNullOrWhiteSpace(tsDns))
        {
            TerminalUi.Error("Tailscale is not running or not logged in on this machine.");
            Console.WriteLine($"  Run {TerminalUi.Cyan}sudo tailscale up{TerminalUi.Reset} first, then try again.");
            TerminalUi.Pause();
            return;
        }

        Console.WriteLine($"  {TerminalUi.Bold}Detected Tailscale MagicDNS:{TerminalUi.Reset} {TerminalUi.Cyan}https://{tsDns}{TerminalUi.Reset}");
        Console.WriteLine($"  {TerminalUi.Muted}Tailscale Funnel routes public internet HTTPS traffic (port 443) straight to Penrill Server (port {state.Port}){TerminalUi.Reset}");
        Console.WriteLine($"  {TerminalUi.Muted}through your outbound Tailscale connection — bypassing Starlink Mini CGNAT without any port forwarding!{TerminalUi.Reset}");
        Console.WriteLine();

        if (!TerminalUi.Confirm($"Enable public internet access via Tailscale Funnel at https://{tsDns}?",
            yesLabel: $"Yes, enable Tailscale Funnel (https://{tsDns})"))
            return;

        Console.WriteLine();
        TerminalUi.Info($"Running: tailscale funnel --bg https+insecure://127.0.0.1:{state.Port}");
        Console.WriteLine($"  {TerminalUi.Amber}Note: If Tailscale prints a https://login.tailscale.com/... link below, open it in your browser to authorize Funnel.{TerminalUi.Reset}");
        Console.WriteLine();

        var exitCode = RunProcessWithExitCode("tailscale", ["funnel", "--bg", $"https+insecure://127.0.0.1:{state.Port}"]);
        Console.WriteLine();
        if (exitCode != 0)
        {
            TerminalUi.Warn($"'tailscale funnel' exited with code {exitCode}. Make sure HTTPS Certificates and Funnel are enabled in your Tailscale Admin Console (DNS & Access Controls).");
            TerminalUi.Pause();
            return;
        }

        var resp = await http.PostAsJsonAsync("/cli/json/domain/tailscale-funnel", new CliTailscaleFunnelRequest(tsDns));
        var res = await resp.Content.ReadFromJsonAsync<DomainSetupResultDto>();
        if (res is not null)
        {
            foreach (var step in res.Steps)
                TerminalUi.Success(step);
            Console.WriteLine();
            if (res.Ok) TerminalUi.Success(res.Message);
            else TerminalUi.Error(res.Message);
        }
        TerminalUi.Pause();
    }

    private static async Task RunDuckDnsSetupWizardAsync(HttpClient http, PublicDomainStatusDto? current, CliStateDto state)
    {
        TerminalUi.Header(
            "Connect Free *.duckdns.org Domain (DuckDNS + Let's Encrypt)",
            "Free forever domain with automatic DDNS and zero-port-80 Let's Encrypt SSL");
        Console.WriteLine();
        Console.WriteLine($"  {TerminalUi.Bold}How to get your free *.duckdns.org domain & token in 20 seconds:{TerminalUi.Reset}");
        Console.WriteLine($"  1. Open {TerminalUi.Cyan}https://www.duckdns.org{TerminalUi.Reset} and sign in with {TerminalUi.Bold}GitHub, Google, or Reddit{TerminalUi.Reset} (1 click).");
        Console.WriteLine($"  2. Type a subdomain name (e.g. {TerminalUi.Bold}yaroslav-penrill{TerminalUi.Reset}) and click {TerminalUi.Bold}add domain{TerminalUi.Reset}.");
        Console.WriteLine($"  3. Copy the {TerminalUi.Bold}token{TerminalUi.Reset} shown at the top of the DuckDNS page.");
        Console.WriteLine();

        var defaultDom = current?.Domain is { } cd && cd.EndsWith(".duckdns.org", StringComparison.OrdinalIgnoreCase)
            ? cd
            : null;
        var domainInput = TerminalUi.Prompt(
            "Your DuckDNS subdomain or full domain (e.g. 'yaroslav-penrill')",
            defaultValue: defaultDom,
            placeholder: "e.g. yaroslav-penrill or yaroslav-penrill.duckdns.org",
            validate: v => DuckDnsClient.NormalizeDomain(v) is not null
                ? null
                : "Enter a valid DuckDNS subdomain (letters, digits, hyphens).");
        if (domainInput is null) return;

        var token = TerminalUi.Prompt(
            "Your DuckDNS token (from the top of https://www.duckdns.org)",
            placeholder: "e.g. a7c4d0ad-114e-40ef-ba1d-d217904a50f2",
            validate: v => v.Trim().Length >= 10 ? null : "Paste your token from https://www.duckdns.org.");
        if (token is null) return;

        var fullDomain = DuckDnsClient.NormalizeDomain(domainInput)!.Value.FullDomain;
        var localIp = current?.LocalIp ?? ServerAddress.PrimaryAddress().ToString();
        var publicIp = current?.PublicIp ?? "auto-detect";
        var tsNet = state.DiscoveredNetworks.FirstOrDefault(n => n.Kind == "tailscale");
        var tsIp = tsNet is not null && Uri.TryCreate(tsNet.Origin, UriKind.Absolute, out var tsUri) ? tsUri.Host : null;

        var ipOptions = new List<SelectOption<string>>
        {
            new($"🌐 Public Internet IP ({publicIp}) — Auto-DDNS", "public", "For routers with a public IP & port forwarding"),
            new($"🏠 Local LAN IP ({localIp})", localIp, $"Trusted Let's Encrypt HTTPS on your Wi-Fi / LAN (works behind Starlink/CGNAT!)")
        };
        if (!string.IsNullOrWhiteSpace(tsIp))
        {
            ipOptions.Add(new($"🔒 Tailscale IP ({tsIp})", tsIp, "Trusted Let's Encrypt HTTPS across your Tailscale devices"));
        }

        var (ipOk, chosenIpMode) = TerminalUi.Select($"Which IP address should {fullDomain} point to?", ipOptions);
        if (!ipOk) return;
        var targetIpOverride = chosenIpMode == "public" ? null : chosenIpMode;

        Console.WriteLine();
        TerminalUi.Info($"Configuring {fullDomain}, updating DNS A record, and requesting a Let's Encrypt SSL certificate via DNS-01...");
        Console.WriteLine($"  {TerminalUi.Muted}(Waiting for DNS propagation and Let's Encrypt verification — usually takes 20–45 seconds){TerminalUi.Reset}");
        Console.WriteLine();

        var resp = await http.PostAsJsonAsync(
            "/cli/json/domain/duckdns",
            new CliDuckDnsDomainRequest(fullDomain, token.Trim(), ForceRenew: false, TargetIp: targetIpOverride));
        var res = await resp.Content.ReadFromJsonAsync<DomainSetupResultDto>();
        PrintDomainSetupResult(res);
        TerminalUi.Pause();
    }

    private static async Task RunDesecSetupWizardAsync(HttpClient http, PublicDomainStatusDto? current)
    {
        TerminalUi.Header(
            "Connect Free *.dedyn.io Domain (deSEC + Let's Encrypt)",
            "Non-profit German DNS with automatic DDNS and zero-port-80 Let's Encrypt SSL");
        Console.WriteLine();
        Console.WriteLine($"  {TerminalUi.Bold}How to get a free forever *.dedyn.io domain in 30 seconds:{TerminalUi.Reset}");
        Console.WriteLine($"  1. Open {TerminalUi.Cyan}https://desec.io/signup{TerminalUi.Reset} in your browser (choose 'DynDNS', enter email & domain name).");
        Console.WriteLine($"  2. Click the confirmation link sent to your email to set your password.");
        Console.WriteLine($"  3. Create an API Token at {TerminalUi.Cyan}https://desec.io/tokens{TerminalUi.Reset} — OR sign in below and Penrill will create one for you.");
        Console.WriteLine();

        var (authSelected, authMode) = TerminalUi.Select<string>(
            "How would you like to connect your deSEC account?",
            [
                new("🔑 Paste a deSEC API Token (recommended)", "token", "Copy token from https://desec.io/tokens"),
                new("📧 Sign in once with deSEC Email & Password", "login", "Penrill creates an API token automatically and never saves your password"),
                new("← Cancel", "cancel")
            ]);
        if (!authSelected || authMode == "cancel") return;

        string? token = null;
        string? email = null;
        string? password = null;

        if (authMode == "token")
        {
            token = TerminalUi.Prompt(
                "deSEC API Token",
                placeholder: "paste your token from https://desec.io/tokens",
                validate: v => v.Trim().Length >= 10 ? null : "Enter a valid deSEC API token.");
            if (token is null) return;
        }
        else
        {
            email = TerminalUi.Prompt(
                "deSEC Account Email",
                validate: v => AdminService.CleanEmail(v) is not null ? null : "Enter the email address you registered on desec.io.");
            if (email is null) return;
            password = TerminalUi.PromptPassword("deSEC Account Password", confirm: false, minLength: 1);
            if (password is null) return;
        }

        var domainInput = TerminalUi.Prompt(
            "Your *.dedyn.io domain name (or short name, e.g. 'myboard')",
            defaultValue: current?.Domain,
            placeholder: "e.g. myboard.dedyn.io",
            validate: v => DesecClient.NormalizeDomain(v, defaultToDedyn: true) is not null
                ? null
                : "Enter a valid name (e.g. 'myboard' or 'myboard.dedyn.io').");
        if (domainInput is null) return;

        var normalizedDomain = DesecClient.NormalizeDomain(domainInput, defaultToDedyn: true)!;
        Console.WriteLine();
        TerminalUi.Info($"Provisioning {normalizedDomain}, updating DDNS IP, and requesting a Let's Encrypt certificate via DNS-01...");
        Console.WriteLine($"  {TerminalUi.Muted}(Waiting for DNS propagation and Let's Encrypt verification — usually takes 20–45 seconds){TerminalUi.Reset}");
        Console.WriteLine();

        var resp = await http.PostAsJsonAsync(
            "/cli/json/domain/desec",
            new CliDesecDomainRequest(normalizedDomain, token, email, password, ForceRenew: false));
        var res = await resp.Content.ReadFromJsonAsync<DomainSetupResultDto>();
        PrintDomainSetupResult(res);
        TerminalUi.Pause();
    }

    private static async Task RunCustomCertWizardAsync(HttpClient http, PublicDomainStatusDto? current)
    {
        TerminalUi.Header("Custom Domain with Existing SSL Certificate", "Provide your domain name and PEM certificate files");
        Console.WriteLine();
        var domainInput = TerminalUi.Prompt(
            "Public domain hostname",
            defaultValue: current?.Domain,
            placeholder: "e.g. boards.example.com",
            validate: v => DesecClient.NormalizeDomain(v, defaultToDedyn: false) is not null
                ? null
                : "Enter a full domain name (e.g. boards.example.com).");
        if (domainInput is null) return;

        var chainPath = TerminalUi.Prompt(
            "Path to fullchain.pem (certificate + intermediate CA)",
            placeholder: "/etc/letsencrypt/live/boards.example.com/fullchain.pem",
            validate: v => File.Exists(v.Trim()) ? null : "File does not exist on disk.");
        if (chainPath is null) return;

        var keyPath = TerminalUi.Prompt(
            "Path to privkey.pem (private key)",
            placeholder: "/etc/letsencrypt/live/boards.example.com/privkey.pem",
            validate: v => File.Exists(v.Trim()) ? null : "File does not exist on disk.");
        if (keyPath is null) return;

        Console.WriteLine();
        var resp = await http.PostAsJsonAsync(
            "/cli/json/domain/custom-cert",
            new CliCustomCertRequest(domainInput, chainPath.Trim(), keyPath.Trim()));
        var res = await resp.Content.ReadFromJsonAsync<DomainSetupResultDto>();
        PrintDomainSetupResult(res);
        TerminalUi.Pause();
    }

    private static void PrintDomainSetupResult(DomainSetupResultDto? res)
    {
        if (res is null)
        {
            TerminalUi.Error("Did not receive a response from the server.");
            return;
        }
        foreach (var step in res.Steps)
        {
            TerminalUi.Success(step);
        }
        Console.WriteLine();
        if (!res.Ok)
        {
            TerminalUi.Error(res.Message);
            return;
        }

        TerminalUi.Success(res.Message);
        if (!res.Reachable && res.Origin is not null)
        {
            Console.WriteLine();
            PrintRouterPortForwardingGuide(res.Origin, res.PublicIp, res.LocalIp, res.Port);
        }
    }

    private static void PrintRouterPortForwardingGuide(string origin, string? publicIp, string localIp, int port)
    {
        TerminalUi.Warn($"One final step on your home router: forward TCP port {port} to this server.");
        TerminalUi.DrawBox(
            "Router Port Forwarding Rule Needed",
            "TCP",
            [
                ("Public URL:", $"{TerminalUi.Cyan}{origin}{TerminalUi.Reset}"),
                ("Public IPv4:", publicIp ?? "(detected automatically)"),
                ("Protocol:", "TCP"),
                ("External Port:", $"{TerminalUi.Bold}{port}{TerminalUi.Reset}"),
                ("Internal IP:", $"{TerminalUi.Bold}{localIp}{TerminalUi.Reset}"),
                ("Internal Port:", $"{TerminalUi.Bold}{port}{TerminalUi.Reset}"),
                ("SSL Cert:", $"{TerminalUi.Green}Already issued & installed! Works as soon as port {port} is forwarded.{TerminalUi.Reset}")
            ]);
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
            placeholder: "leave empty for default penrill.com");
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

    private static void RunProcess(string fileName, string[] args) =>
        RunProcessWithExitCode(fileName, args);

    private static int RunProcessWithExitCode(string fileName, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
            return proc?.ExitCode ?? 1;
        }
        catch (Exception ex)
        {
            TerminalUi.Error($"Failed to run '{fileName}': {ex.Message}");
            return 1;
        }
    }
}
