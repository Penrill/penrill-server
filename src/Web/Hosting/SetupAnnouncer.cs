using Infrastructure.Data;
using System.Text;
using Application.Accounts;
using Domain.Constants;
using Domain.Enums;

namespace Web.Hosting;

/// <summary>
/// While nobody owns the server, prints a claim link and its QR code on start (to the journal) and
/// keeps it in <c>setup.txt</c>, readable only by the service and root. <c>penrill-server setup</c>
/// asks for a new one. Once claimed, the file is removed.
/// </summary>
public sealed class SetupAnnouncer(IServiceScopeFactory scopes, ServerAddress address, ServerData data, IHostApplicationLifetime lifetime, ILogger<SetupAnnouncer> logger)
    : IHostedService
{
    private string FilePath => Path.Combine(data.Directory, "setup.txt");

    public Task StartAsync(CancellationToken cancel)
    {
        lifetime.ApplicationStarted.Register(() => _ = Announce());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancel) => Task.CompletedTask;

    /// <summary>The setup message with a new claim link, or null when the server already has an owner.</summary>
    public async Task<string?> Message(CancellationToken cancel)
    {
        using var scope = scopes.CreateScope();
        if (await scope.ServiceProvider.GetRequiredService<GrantService>().CreateClaim(cancel) is not { } token)
        {
            File.Delete(FilePath);
            return null;
        }
        var url = $"{address.DefaultOrigin}/setup#k={token}";
        var text = new StringBuilder()
            .AppendLine()
            .AppendLine("Penrill Server is running. Finish setup in a browser on any device in this network:")
            .AppendLine()
            .AppendLine("  " + url)
            .AppendLine()
            .Append(Qr.Terminal(url));
        if (address.ShortPin is { } pin)
            text.AppendLine().AppendLine($"The browser will warn about the certificate. It is this server's own; its fingerprint starts with {pin}.");
        var message = text.ToString();
        PrivateFile.Write(FilePath, message);
        return message;
    }

    private async Task Announce()
    {
        try
        {
            if (await Message(lifetime.ApplicationStopping) is { } message) Console.Out.WriteLine(message);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "Could not create the setup link");
        }
    }
}
