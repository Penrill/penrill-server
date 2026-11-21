using Microsoft.AspNetCore.Mvc;
using Penrill.Core.Accounts;
using Penrill.Core.Admin;
using Penrill.Server.Hosting;

namespace Penrill.Server.Pages.Admin;

public sealed class IndexModel(AdminService admin, ServerAddress address) : PenrillPage
{
    public IReadOnlyList<AccountInfo> Users { get; private set; } = [];
    public IReadOnlyList<Grant> Invites { get; private set; } = [];
    [TempData] public string? LinkTitle { get; set; }
    [TempData] public string? LinkUrl { get; set; }

    [BindProperty] public Role InviteRole { get; set; } = Role.Member;
    [BindProperty] public int InviteUses { get; set; } = 1;
    [BindProperty] public int InviteDays { get; set; } = 7;

    public async Task OnGet()
    {
        Users = await admin.Users(HttpContext.RequestAborted);
        Invites = await admin.Invites(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostInvite()
    {
        var invite = await admin.Invite(Me, InviteRole, TimeSpan.FromDays(Math.Clamp(InviteDays, 1, 90)), InviteUses, HttpContext.RequestAborted);
        LinkTitle = "Invite link. Send it to the person who should join:";
        LinkUrl = $"{address.Origin(Request)}/invite/{invite.Token}";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteInvite(string id)
    {
        await admin.DeleteInvite(id, HttpContext.RequestAborted);
        return Back("Invite deleted.");
    }

    public async Task<IActionResult> OnPostRole(string user, Role role) =>
        Back(await admin.SetRole(user, role, HttpContext.RequestAborted) ? $"{user} is now {role.ToString().ToLowerInvariant()}." : "The owner's role cannot change.");

    public async Task<IActionResult> OnPostDisable(string user, bool disabled) =>
        Back(await admin.SetDisabled(user, disabled, HttpContext.RequestAborted)
            ? disabled ? $"{user} is disabled and signed out everywhere." : $"{user} can sign in again."
            : "The owner cannot be disabled.");

    public async Task<IActionResult> OnPostReset(string user)
    {
        if (await admin.ResetLink(Me, user, HttpContext.RequestAborted) is not { } reset) return Back();
        LinkTitle = $"Password reset link for {user}, valid for one hour:";
        LinkUrl = $"{address.Origin(Request)}/reset/{reset.Token}";
        return RedirectToPage();
    }
}
