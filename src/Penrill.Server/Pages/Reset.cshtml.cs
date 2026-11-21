using Microsoft.AspNetCore.Mvc;
using Penrill.Core.Accounts;

namespace Penrill.Server.Pages;

public sealed class ResetModel(GrantService grants) : PenrillPage
{
    [BindProperty(SupportsGet = true)] public string? Token { get; set; }
    [BindProperty] public string? Password { get; set; }
    public string? For { get; private set; }

    public async Task OnGet() => For = await grants.Peek(Token, HttpContext.RequestAborted) is { Kind: GrantKind.Reset } grant ? grant.Subject : null;

    public async Task<IActionResult> OnPost()
    {
        var result = await grants.Reset(Token, Password, HttpContext.RequestAborted);
        if (result.Status == RedeemStatus.Ok)
        {
            Notice("Password changed. Sign in with the new one; your devices need to be connected again.");
            return Redirect("/login");
        }
        await OnGet();
        Error = result.Status == RedeemStatus.Expired ? "This link has expired or was already used." : "Use at least 10 characters.";
        return Page();
    }
}
