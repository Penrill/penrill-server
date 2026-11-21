using Microsoft.AspNetCore.Mvc;
using Penrill.Core.Accounts;

namespace Penrill.Server.Pages;

public sealed class InviteModel(GrantService grants, AccountService accounts) : PenrillPage
{
    [BindProperty(SupportsGet = true)] public string? Token { get; set; }
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Password { get; set; }
    public bool Valid { get; private set; }

    public async Task OnGet() => Valid = await grants.Peek(Token, HttpContext.RequestAborted) is { Kind: GrantKind.Invite };

    public async Task<IActionResult> OnPost()
    {
        var result = await grants.AcceptInvite(Token, Name, Password, HttpContext.RequestAborted);
        if (result.Status != RedeemStatus.Ok)
        {
            Valid = result.Status != RedeemStatus.Expired;
            Error = Explain(result.Status);
            return Page();
        }
        await StartSession(accounts, Name, Password);
        Notice($"Welcome, {result.User}. Connect Penrill to this server below.");
        return Redirect("/");
    }
}
