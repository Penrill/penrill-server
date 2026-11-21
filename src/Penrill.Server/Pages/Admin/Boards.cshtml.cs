using Microsoft.AspNetCore.Mvc;
using Penrill.Core.Accounts;
using Penrill.Core.Admin;

namespace Penrill.Server.Pages.Admin;

/// <summary>Boards from server versions before accounts have no owner; give each one to someone.</summary>
public sealed class BoardsModel(AdminService admin) : PenrillPage
{
    public IReadOnlyList<OrphanBoard> Orphans { get; private set; } = [];
    public IReadOnlyList<AccountInfo> Users { get; private set; } = [];

    public async Task OnGet()
    {
        Orphans = await admin.Orphans(HttpContext.RequestAborted);
        Users = await admin.Users(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostAssign(string board, string user) =>
        Back(await admin.AssignOwner(board, user, HttpContext.RequestAborted) ? $"The board now belongs to {user}." : "That did not work. Reload and try again.");
}
