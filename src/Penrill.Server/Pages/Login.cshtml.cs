using Microsoft.AspNetCore.Mvc;
using Penrill.Core.Accounts;
using Penrill.Server.Hosting;

namespace Penrill.Server.Pages;

public sealed class LoginModel(AccountService accounts, GrantService grants) : PenrillPage
{
    [BindProperty(SupportsGet = true)] public string? Return { get; set; }
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Password { get; set; }

    public async Task<IActionResult> OnGet() =>
        await grants.Unclaimed(HttpContext.RequestAborted) ? Redirect("/setup") : Page();

    public async Task<IActionResult> OnPost()
    {
        if (await StartSession(accounts, Name, Password)) return LocalRedirect(WebSessionHandler.SafeReturn(Return));
        Error = "Wrong username or password.";
        Password = null;
        return Page();
    }
}
