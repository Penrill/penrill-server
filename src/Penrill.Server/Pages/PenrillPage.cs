using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Penrill.Api.Hosting;
using Penrill.Core.Accounts;
using Penrill.Server.Hosting;

namespace Penrill.Server.Pages;

public abstract class PenrillPage : PageModel
{
    public string? Error { get; protected set; }

    protected string Me => User.UserName();

    protected void Notice(string text) => TempData["Notice"] = text;

    /// <summary>Signs in with a fresh web session after a claim, an invite or a password sign-in.</summary>
    protected async Task<bool> StartSession(AccountService accounts, string? name, string? password)
    {
        var result = await accounts.SignIn(name, password, HttpContext.RequestAborted);
        if (result.Status != SignInStatus.Ok) return false;
        WebSessionHandler.SignIn(HttpContext, result.Token!);
        return true;
    }

    protected static string Explain(RedeemStatus status) => status switch
    {
        RedeemStatus.Taken => "That username is taken.",
        RedeemStatus.Expired => "This link has expired or was already used.",
        _ => "Use 3 to 32 lowercase letters, digits, - or _ for the username, and at least 10 characters for the password."
    };

    protected IActionResult Back(string? notice = null)
    {
        if (notice is not null) Notice(notice);
        return RedirectToPage();
    }
}
