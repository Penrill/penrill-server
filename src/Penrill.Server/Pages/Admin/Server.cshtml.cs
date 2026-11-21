using Microsoft.AspNetCore.Mvc;
using Penrill.Core.Admin;
using Penrill.Server.Hosting;

namespace Penrill.Server.Pages.Admin;

public sealed class ServerModel(AdminService admin, ServerAddress address) : PenrillPage
{
    [BindProperty] public string? Name { get; set; }
    public ServerCounts Counts { get; private set; } = new(0, 0, 0);
    public ServerAddress Address => address;
    public string Origin => address.Origin(Request);
    public string Version => typeof(ServerModel).Assembly.GetName().Version?.ToString(3) ?? "";

    public async Task OnGet()
    {
        Name = await admin.ServerName(HttpContext.RequestAborted);
        Counts = await admin.Counts(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostRename()
    {
        if (await admin.Rename(Name, HttpContext.RequestAborted)) return Back("Renamed.");
        Error = "Use 1 to 64 characters.";
        await OnGet();
        return Page();
    }

    /// <summary>Streams the archive straight to the browser, built while the server keeps running.</summary>
    public async Task OnGetBackup()
    {
        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = $"attachment; filename=\"penrill-backup-{DateTime.Now:yyyy-MM-dd}.zip\"";
        var buffer = new MemoryStream();
        await admin.Backup(buffer, HttpContext.RequestAborted);
        buffer.Position = 0;
        await buffer.CopyToAsync(Response.Body, HttpContext.RequestAborted);
    }
}
