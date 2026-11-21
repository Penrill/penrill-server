using System.Net.Http.Json;
using System.Net.Sockets;

namespace Penrill.Server.Cli;

/// <summary>
/// <c>penrill-server [VERB ...]</c>: launches the interactive terminal configurator or sends a command
/// to the running service over its Unix admin socket. File permissions on the socket are the
/// authentication, so run it with sudo.
/// </summary>
public static class CommandLine
{
    public const string DefaultSocket = "/run/penrill-server/admin.sock";

    public static bool IsCommand(string[] args)
    {
        if (args.Length > 0)
            return !args[0].StartsWith('-') || args[0] is "--help" or "-h" or "--version" or "-v";
        return TerminalUi.IsInteractive && HasInstalledSocket();
    }

    public static string SocketPath(IConfiguration configuration, string dataDirectory) =>
        Path.GetFullPath(configuration["PENRILL_ADMIN_SOCKET"] is { Length: > 0 } path ? path : Path.Combine(dataDirectory, "admin.sock"));

    private static string ResolveClientSocket() =>
        Environment.GetEnvironmentVariable("PENRILL_ADMIN_SOCKET") is { Length: > 0 } configured ? configured
        : Environment.GetEnvironmentVariable("SNAP_COMMON") is { Length: > 0 } snapCommon && File.Exists(Path.Combine(snapCommon, "admin.sock")) ? Path.Combine(snapCommon, "admin.sock")
        : File.Exists(DefaultSocket) ? DefaultSocket
        : Environment.GetEnvironmentVariable("SNAP_COMMON") is { Length: > 0 } snapFallback ? Path.Combine(snapFallback, "admin.sock")
        : Path.Combine(AppContext.BaseDirectory, "data", "admin.sock");

    private static bool HasInstalledSocket() =>
        File.Exists(DefaultSocket) ||
        (Environment.GetEnvironmentVariable("SNAP_COMMON") is { Length: > 0 } snapCommon && File.Exists(Path.Combine(snapCommon, "admin.sock")));

    public static async Task<int> Run(string[] args)
    {
        var verb = args.Length == 0 ? "configure" : args[0];
        if (verb is "--version" or "-v" or "version")
        {
            Console.WriteLine(typeof(CommandLine).Assembly.GetName().Version?.ToString(3) ?? "0.3.0");
            return 0;
        }
        if (verb is "menu" or "ui" or "config") verb = "configure";

        if (verb is "help" or "--help" or "-h" || AdminCommands.All.All(command => command.Name != verb))
        {
            Console.WriteLine("Usage: sudo penrill-server [COMMAND]\n");
            Console.WriteLine("  (no command)                               Launch interactive configurator & control panel");
            foreach (var command in AdminCommands.All) Console.WriteLine($"  {command.Usage,-42} {command.Summary}");
            Console.WriteLine("\nRun without arguments on an installed server (or 'sudo penrill-server configure') for the interactive UI.");
            return verb is "help" or "--help" or "-h" ? 0 : 2;
        }

        if (verb == "uninstall")
        {
            var rest = args.Length > 1 ? args[1..] : [];
            bool? forcePurge = rest.Contains("--purge") ? true : rest.Contains("--keep") ? false : null;
            var skipConfirm = rest.Contains("--yes") || rest.Contains("-y");
            InteractiveConfigurator.RunUninstallInteractive(forcePurge, skipConfirm);
            return 0;
        }

        var socket = ResolveClientSocket();
        using var http = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancel) =>
            {
                var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await client.ConnectAsync(new UnixDomainSocketEndPoint(socket), cancel);
                return new NetworkStream(client, ownsSocket: true);
            }
        }) { BaseAddress = new Uri("http://localhost"), Timeout = TimeSpan.FromMinutes(10) };

        if (verb == "configure")
        {
            try
            {
                return await InteractiveConfigurator.RunAsync(http);
            }
            catch (HttpRequestException error) when (error.InnerException is SocketException socketError)
            {
                PrintSocketError(socketError, socket);
                return 1;
            }
        }

        var callArgs = args.Length > 1 ? args[1..] : [];

        if (verb is "set-password" or "create-user" && TerminalUi.IsInteractive && !callArgs.Contains("--password") && callArgs.Count(a => !a.StartsWith("--", StringComparison.Ordinal)) < 2)
        {
            var user = callArgs.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(user))
                user = TerminalUi.Prompt("Username");
            if (string.IsNullOrWhiteSpace(user)) return 1;
            var pass = TerminalUi.PromptPassword($"Password for '{user}'", confirm: true);
            if (pass is null) return 1;
            callArgs = [.. callArgs.Where(a => a.StartsWith("--", StringComparison.Ordinal)), user, "--password", pass];
        }

        if (verb == "delete-user" && TerminalUi.IsInteractive && !callArgs.Contains("--yes") && !callArgs.Contains("-y"))
        {
            var user = callArgs.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(user) && !TerminalUi.Confirm($"Delete account '{user}' permanently?", yesLabel: $"Yes, delete '{user}'"))
                return 0;
        }

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("/cli/" + verb, callArgs);
        }
        catch (HttpRequestException error) when (error.InnerException is SocketException socketError)
        {
            PrintSocketError(socketError, socket);
            return 1;
        }

        if (verb == "backup")
        {
            var target = callArgs.Length > 0 ? callArgs[0] : $"penrill-backup-{DateTime.Now:yyyy-MM-dd}.zip";
            await using var file = File.Create(target);
            await response.Content.CopyToAsync(file);
            Console.WriteLine($"Backup written to {Path.GetFullPath(target)}");
            return 0;
        }
        var text = await response.Content.ReadAsStringAsync();
        (response.IsSuccessStatusCode ? Console.Out : Console.Error).Write(text);
        return response.IsSuccessStatusCode ? 0 : 1;
    }

    private static void PrintSocketError(SocketException socketError, string socket)
    {
        var start = Environment.GetEnvironmentVariable("SNAP") is not null ? "sudo snap start penrill-server" : "sudo systemctl start penrill-server";
        Console.Error.WriteLine(socketError.SocketErrorCode is SocketError.AccessDenied
            ? "Permission denied. Run it with sudo: sudo penrill-server"
            : $"Penrill Server is not running here (no socket at {socket}). Start it with: {start}");
    }
}
