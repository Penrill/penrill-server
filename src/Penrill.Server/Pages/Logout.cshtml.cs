using Microsoft.AspNetCore.Mvc;
using Penrill.Core.Accounts;
using Penrill.Server.Hosting;

namespace Penrill.Server.Pages;

public sealed class LogoutModel(AccountService accounts) : PenrillPage
{
    public IActionResult OnGet() => Redirect("/");

    public async Task<IActionResult> OnPost()
    {
        await WebSessionHandler.SignOut(HttpContext, accounts);
        return Redirect("/login");
    }
}
