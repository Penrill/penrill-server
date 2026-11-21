using Microsoft.AspNetCore.Mvc;
using Penrill.Core.Linking;

namespace Penrill.Server.Pages;

/// <summary>Where an editor sends people to allow it: shows which device asks, by the code both screens show.</summary>
public sealed class LinkModel(LinkService links) : PenrillPage
{
    [BindProperty(SupportsGet = true)] public string? Code { get; set; }
    public LinkInfo? Pending { get; private set; }
    public string? Done { get; private set; }

    public void OnGet()
    {
        Pending = links.Describe(Code) is { State: LinkState.Pending } info ? info : null;
        if (Code is { Length: > 0 } && Pending is null) Error = "No device is waiting with this code. It may have expired; start again in Penrill.";
    }

    public IActionResult OnPost(bool allow)
    {
        var info = links.Describe(Code);
        if (info is null || !links.Decide(Code, Me, allow))
        {
            Error = "No device is waiting with this code. It may have expired; start again in Penrill.";
            return Page();
        }
        Done = allow ? $"{info.Device} is connected. You can close this page and go back to Penrill." : $"{info.Device} was not connected.";
        return Page();
    }
}
