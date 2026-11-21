using System.Net;
using System.Net.Sockets;
using System.Text;
using Penrill.Api.Hosting;
using QRCoder;

namespace Penrill.Server.Hosting;

/// <summary>
/// How people reach this server. <see cref="Local"/> listens on loopback over plain HTTP (development,
/// or behind a reverse proxy on the same machine); <see cref="Lan"/> listens on every interface with
/// the pinned self-signed certificate. Domain and Penrill address modes slot in here later.
/// </summary>
public enum AddressMode { Local, Lan }

public sealed class ServerAddress(AddressMode mode, string localOrigin, int port, ServerCertificate? certificate) : IPublicAddress
{
    public AddressMode Mode => mode;

    public string? Pin => certificate?.Pin;

    public string? ShortPin => certificate?.ShortPin;

    /// <summary>The origin to print when there is no request to go by: the address this machine has on its network.</summary>
    public string DefaultOrigin => mode == AddressMode.Lan ? $"https://{Bracket(PrimaryAddress())}:{port}" : localOrigin;

    /// <summary>Whatever name the visitor used to get here works for them, except loopback, which only works on this machine.</summary>
    public string Origin(HttpRequest request)
    {
        if (mode == AddressMode.Local) return $"{request.Scheme}://{request.Host}";
        var host = request.Host.Host;
        return host is "localhost" || IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)
            ? DefaultOrigin
            : $"https://{request.Host}";
    }

    /// <summary>The address of the interface that holds the default route. No packet is sent: connecting a UDP socket only picks a route.</summary>
    public static IPAddress PrimaryAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9));
            if (socket.LocalEndPoint is IPEndPoint { Address: var address } && !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any)) return address;
        }
        catch (SocketException)
        {
        }
        return ServerCertificate.LocalAddresses().FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? IPAddress.Loopback;
    }

    private static string Bracket(IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
}

/// <summary>
/// The link that connects Penrill to this server. It goes to the connect page on penrill.com, which
/// knows what the visitor has: it opens the app, offers Penrill in the browser, or offers a download.
/// The server only knows its own address. The address travels in the fragment, so it never reaches
/// penrill.com. Development points <c>PENRILL_CONNECT_PAGE</c> at a local web editor instead.
/// </summary>
public sealed class ConnectLinks(string page)
{
    public const string DefaultPage = "https://penrill.com/connect";

    public string For(string origin, string? pin) =>
        page.Split('#')[0] + "#server=" + Uri.EscapeDataString(origin) + (pin is null ? "" : "&pin=" + Uri.EscapeDataString(pin));
}

public static class Qr
{
    public static string Svg(string text)
    {
        using var data = QRCodeGenerator.GenerateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(4, "#18181b", "#ffffff", drawQuietZones: true, SvgQRCode.SizingMode.ViewBoxAttribute);
    }

    /// <summary>Two modules per character with half blocks, dark on light, so it scans from a dark terminal too.</summary>
    public static string Terminal(string text)
    {
        using var data = QRCodeGenerator.GenerateQrCode(text, QRCodeGenerator.ECCLevel.L);
        var matrix = data.ModuleMatrix;
        var size = matrix.Count;
        bool Dark(int row, int column) => row < size && matrix[row][column];
        var output = new StringBuilder();
        for (var row = 0; row < size; row += 2)
        {
            for (var column = 0; column < size; column++)
                output.Append((Dark(row, column), Dark(row + 1, column)) switch
                {
                    (false, false) => '█',
                    (true, false) => '▄',
                    (false, true) => '▀',
                    _ => ' '
                });
            output.Append('\n');
        }
        return output.ToString();
    }
}
