using Penrill.Core.Sharing;
using Penrill.Core.Accounts;

namespace Penrill.Core.Admin;

public sealed record OrphanBoard(string Id, string Title, DateTimeOffset Updated);

public sealed record ServerCounts(int Users, int Boards, int Devices);

/// <summary>Administration queries that do not belong to everyday board traffic.</summary>
public interface IAdminStore
{
    Task<IReadOnlyList<OrphanBoard>> Orphans(CancellationToken cancel);
    Task<bool> AssignOwner(string board, string user, CancellationToken cancel);
    Task<ServerCounts> Counts(CancellationToken cancel);
    /// <summary>Writes a consistent copy of the database and its assets as a zip archive.</summary>
    Task Backup(Stream output, CancellationToken cancel);
}

/// <summary>Small persistent server settings, such as its display name.</summary>
public interface ISettingsStore
{
    Task<string?> Get(string key, CancellationToken cancel);
    Task Set(string key, string value, CancellationToken cancel);
}

public static class SettingKeys
{
    public const string ServerName = "server.name";
    /// <summary>Who runs the server. Kept for notices about it (updates, security), not used to sign in.</summary>
    public const string ContactEmail = "server.email";
}

/// <summary>
/// One entry point for everything an administrator can do (Facade). The web admin pages and the
/// command line both call it, so the two front ends cannot drift apart.
/// </summary>
public sealed class AdminService(AccountService accounts, IAccountStore store, GrantService grants, IAdminStore admin, ISettingsStore settings, ITemplateStore templates)
{
    public const int MaxServerName = 64;

    public Task<IReadOnlyList<AccountInfo>> Users(CancellationToken cancel) => store.List(cancel);

    public Task<IReadOnlyList<DeviceInfo>> Devices(CancellationToken cancel) => accounts.Devices(null, cancel);

    public Task<bool> RevokeDevice(string id, CancellationToken cancel) => accounts.RevokeDevice(null, id, cancel);

    public Task<GrantToken> Invite(string by, Role role, TimeSpan? lifetime, int uses, CancellationToken cancel) =>
        grants.CreateInvite(by, role, lifetime, uses, cancel);

    public Task<IReadOnlyList<Grant>> Invites(CancellationToken cancel) => grants.Invites(cancel);

    public Task<bool> DeleteInvite(string id, CancellationToken cancel) => grants.Delete(id, cancel);

    public async Task<GrantToken?> ResetLink(string by, string? user, CancellationToken cancel) =>
        AccountService.Normalize(user) is { } name ? await grants.CreateReset(by, name, cancel) : null;

    /// <summary>The owner role is only ever given by the claim; it cannot be granted or taken here.</summary>
    public async Task<bool> SetRole(string? user, Role role, CancellationToken cancel)
    {
        if (role == Role.Owner || AccountService.Normalize(user) is not { } name) return false;
        if (await store.Find(name, cancel) is not { Role: not Role.Owner }) return false;
        return await store.SetRole(name, role, cancel);
    }

    public async Task<bool> SetDisabled(string? user, bool disabled, CancellationToken cancel) =>
        AccountService.Normalize(user) is { } name && await accounts.SetDisabled(name, disabled, cancel);

    public Task<IReadOnlyList<OrphanBoard>> Orphans(CancellationToken cancel) => admin.Orphans(cancel);

    public async Task<bool> AssignOwner(string board, string? user, CancellationToken cancel) =>
        AccountService.Normalize(user) is { } name && await admin.AssignOwner(board, name, cancel);

    public Task<ServerCounts> Counts(CancellationToken cancel) => admin.Counts(cancel);

    /// <summary>Every template people shared on this server, in any folder.</summary>
    public Task<IReadOnlyList<SharedTemplate>> Templates(CancellationToken cancel) => templates.All(cancel);

    /// <summary>Stops sharing a template, whoever shared it. Copies people added keep their last version.</summary>
    public Task<bool> RemoveTemplate(string by, string id, CancellationToken cancel) => templates.Remove(by, true, id, cancel);

    public Task Backup(Stream output, CancellationToken cancel) => admin.Backup(output, cancel);

    public async Task<string> ServerName(CancellationToken cancel) =>
        await settings.Get(SettingKeys.ServerName, cancel) is { Length: > 0 } name ? name : Environment.MachineName;

    public async Task<bool> Rename(string? name, CancellationToken cancel)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length is 0 or > MaxServerName || clean.Any(char.IsControl)) return false;
        await settings.Set(SettingKeys.ServerName, clean, cancel);
        return true;
    }

    public const int MaxEmail = 254;

    public Task<string?> ContactEmail(CancellationToken cancel) => settings.Get(SettingKeys.ContactEmail, cancel);

    public async Task<bool> SetContactEmail(string? email, CancellationToken cancel)
    {
        if (CleanEmail(email) is not { } clean) return false;
        await settings.Set(SettingKeys.ContactEmail, clean, cancel);
        return true;
    }

    /// <summary>A plausible address (something@domain), trimmed; deliverability is not checked.</summary>
    public static string? CleanEmail(string? email)
    {
        var clean = (email ?? "").Trim();
        var at = clean.IndexOf('@');
        return clean.Length <= MaxEmail && at > 0 && at == clean.LastIndexOf('@') && clean.IndexOf('.', at) > at + 1 &&
            !clean.EndsWith('.') && !clean.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ? clean : null;
    }
}
