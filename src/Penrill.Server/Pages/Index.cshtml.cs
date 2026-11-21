using Penrill.Server.Hosting;

namespace Penrill.Server.Pages;

public sealed class IndexModel(ServerAddress address, ConnectLinks links) : PenrillPage
{
    public string Origin { get; private set; } = "";
    public string Link { get; private set; } = "";
    public string? ShortPin => address.ShortPin;

    public void OnGet()
    {
        Origin = address.Origin(Request);
        Link = links.For(Origin, address.Pin);
    }
}
