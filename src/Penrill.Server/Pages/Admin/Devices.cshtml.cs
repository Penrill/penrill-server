using Microsoft.AspNetCore.Mvc;
using Penrill.Core.Accounts;
using Penrill.Core.Admin;

namespace Penrill.Server.Pages.Admin;

public sealed class DevicesModel(AdminService admin) : PenrillPage
{
    public IReadOnlyList<DeviceInfo> Devices { get; private set; } = [];

    public async Task OnGet() => Devices = await admin.Devices(HttpContext.RequestAborted);

    public async Task<IActionResult> OnPostRevoke(string id)
    {
        await admin.RevokeDevice(id, HttpContext.RequestAborted);
        return Back("Device disconnected.");
    }
}
