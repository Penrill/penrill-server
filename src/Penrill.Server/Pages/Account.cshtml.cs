using Microsoft.AspNetCore.Mvc;
using Penrill.Api.Hosting;
using Penrill.Core.Accounts;

namespace Penrill.Server.Pages;

public sealed class AccountModel(AccountService accounts) : PenrillPage
{
    [BindProperty] public string? Current { get; set; }
    [BindProperty] public string? Next { get; set; }
    public IReadOnlyList<DeviceInfo> Devices { get; private set; } = [];

    public async Task OnGet() => Devices = await accounts.Devices(Me, HttpContext.RequestAborted);

    public async Task<IActionResult> OnPostPassword()
    {
        if (await accounts.ChangePassword(Me, Current, Next, HttpContext.RequestAborted)) return Back("Password changed.");
        Error = "The current password is wrong, or the new one is shorter than 10 characters.";
        await OnGet();
        return Page();
    }

    public async Task<IActionResult> OnPostRevoke(string id)
    {
        await accounts.RevokeDevice(Me, id, HttpContext.RequestAborted);
        return Back("Device disconnected.");
    }
}
