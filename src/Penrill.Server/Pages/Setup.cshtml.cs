using Microsoft.AspNetCore.Mvc;
using Penrill.Core.Accounts;
using Penrill.Core.Admin;

namespace Penrill.Server.Pages;

public sealed class SetupModel(GrantService grants, AccountService accounts, AdminService admin, IWebHostEnvironment environment) : PenrillPage
{
    [BindProperty] public string? Token { get; set; }
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string? ServerName { get; set; }

    /// <summary>The link used was stale: the page asks for a new one instead of showing the form.</summary>
    public bool Expired { get; private set; }

    public async Task<IActionResult> OnGet()
    {
        if (!await grants.Unclaimed(HttpContext.RequestAborted)) return Redirect("/");
        ServerName = await admin.ServerName(HttpContext.RequestAborted);
        // Development only (dotnet run, Aspire): the browser on this machine gets the claim directly,
        // so nobody has to dig the link out of the console. Installed servers never do this.
        if (environment.IsDevelopment() && HttpContext.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip))
            Token = await grants.CreateClaim(HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPost()
    {
        var cancel = HttpContext.RequestAborted;
        var result = await grants.Claim(Token, Name, Password, cancel);
        if (result.Status != RedeemStatus.Ok)
        {
            if (result.Status == RedeemStatus.Expired)
            {
                Expired = true;
                Token = null;
                ModelState.Remove(nameof(Token));
            }
            else Error = Explain(result.Status);
            return Page();
        }
        await admin.Rename(ServerName, cancel);
        await StartSession(accounts, Name, Password);
        Notice("Your server is ready. Connect Penrill to it below.");
        return Redirect("/");
    }
}
