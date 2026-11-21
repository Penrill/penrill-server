using System.Net.Http.Json;
using System.Net.Sockets;

namespace Penrill.Server.Cli;

/// <summary>
/// <c>penrill-server VERB ...</c>: sends the verb to the running service over its admin socket and
/// prints the answer. File permissions on the socket are the authentication, so run it with sudo.
/// </summary>
public static class CommandLine
{
    public const string DefaultSocket = "/run/penrill-server/admin.sock";

    public static bool IsCommand(string[] args) => args.Length > 0 && !args[0].StartsWith('-');

    public static string SocketPath(IConfiguration configuration, string dataDirectory) =>
        Path.GetFullPath(configuration["PENRILL_ADMIN_SOCKET"] is { Length: > 0 } path ? path : Path.Combine(dataDirectory, "admin.sock"));

    public static async Task<int> Run(string[] args)
    {
        var verb = args[0];
        if (verb is "help" or "--help" || AdminCommands.All.All(command => command.Name != verb))
        {
            Console.WriteLine("Usage: penrill-server COMMAND\n");
            foreach (var command in AdminCommands.All) Console.WriteLine($"  {command.Usage,-42} {command.Summary}");
            Console.WriteLine("\nWithout a command, runs the server.");
            return verb is "help" or "--help" ? 0 : 2;
        }
        var socket = Environment.GetEnvironmentVariable("PENRILL_ADMIN_SOCKET") is { Length: > 0 } configured ? configured
            : Environment.GetEnvironmentVariable("SNAP_COMMON") is { Length: > 0 } snapCommon ? Path.Combine(snapCommon, "admin.sock")
            : File.Exists(DefaultSocket) ? DefaultSocket
            : Path.Combine(AppContext.BaseDirectory, "data", "admin.sock");
        using var http = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancel) =>
            {
                var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await client.ConnectAsync(new UnixDomainSocketEndPoint(socket), cancel);
                return new NetworkStream(client, ownsSocket: true);
            }
        }) { BaseAddress = new Uri("http://localhost"), Timeout = TimeSpan.FromMinutes(10) };
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("/cli/" + verb, args[1..]);
        }
        catch (HttpRequestException error) when (error.InnerException is SocketException socketError)
        {
            var start = Environment.GetEnvironmentVariable("SNAP") is not null ? "sudo snap start penrill-server" : "sudo systemctl start penrill-server";
            Console.Error.WriteLine(socketError.SocketErrorCode is SocketError.AccessDenied
                ? "Permission denied. Run it with sudo."
                : $"Penrill Server is not running here (no socket at {socket}). Start it with: {start}");
            return 1;
        }
        if (verb == "backup")
        {
            var target = args.Length > 1 ? args[1] : $"penrill-backup-{DateTime.Now:yyyy-MM-dd}.zip";
            await using var file = File.Create(target);
            await response.Content.CopyToAsync(file);
            Console.WriteLine($"Backup written to {Path.GetFullPath(target)}");
            return 0;
        }
        var text = await response.Content.ReadAsStringAsync();
        (response.IsSuccessStatusCode ? Console.Out : Console.Error).Write(text);
        return response.IsSuccessStatusCode ? 0 : 1;
    }
}
