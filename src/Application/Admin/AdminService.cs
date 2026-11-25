using Application.Accounts;
using Application.Common.Interfaces;
using Application.Common.Models;

namespace Application.Admin;

public sealed record OrphanBoard(string Id, string Title, DateTimeOffset Updated);

public sealed record AdminBoard(string Id, string Title, string Owner, long Seq, DateTimeOffset Updated);

public sealed record ServerCounts(int Users, int Boards, int Devices);

/// <summary>A template as the admin pages list it: with the folder's name instead of its id.</summary>
public sealed record SharedTemplate(string Id, string Name, string Author, string? FolderName, DateTimeOffset Updated);

/// <summary>
/// One entry point for everything an administrator can do (Facade). A server's web admin pages and its command line
/// both call it, so the two front ends cannot drift apart.
/// </summary>
public sealed class AdminService(
    AccountService accounts, GrantService grants, IIdentityService identity, IApplicationDbContext context, IPublisher publisher,
    IBackupWriter? backup = null)
{
    public Task<IReadOnlyList<AccountInfo>> Users(CancellationToken cancellationToken) => identity.ListAsync();

    /// <summary>The owner role is only ever given by the claim; asking for it here gives admin.</summary>
    public Task<(Result Result, string? User)> CreateUser(string? user, string? password, Role role, CancellationToken cancellationToken) =>
        accounts.CreateAccount(user, password, role == Role.Owner ? Role.Admin : role);

    public async Task<bool> SetPassword(string? user, string? password, CancellationToken cancellationToken)
    {
        if (AccountRules.NormalizeUserName(user) is not { } name || await identity.FindAsync(name) is null) return false;
        if (!await accounts.SetPassword(name, password)) return false;
        await accounts.RevokeAll(name, cancellationToken);
        return true;
    }

    public async Task<bool> DeleteUser(string? user, CancellationToken cancellationToken) =>
        AccountRules.NormalizeUserName(user) is { } name && await accounts.DeleteAccount(name, cancellationToken);

    public Task<IReadOnlyList<DeviceDto>> Devices(CancellationToken cancellationToken) => accounts.Devices(null, cancellationToken);

    public Task<bool> RevokeDevice(string id, CancellationToken cancellationToken) => accounts.RevokeDevice(null, id, cancellationToken);

    public Task<GrantToken> Invite(string by, Role role, TimeSpan? lifetime, int uses, CancellationToken cancellationToken) =>
        grants.CreateInvite(by, role, lifetime, uses, cancellationToken);

    public Task<IReadOnlyList<Grant>> Invites(CancellationToken cancellationToken) => grants.Invites(cancellationToken);

    public Task<bool> DeleteInvite(string id, CancellationToken cancellationToken) => grants.Delete(id, cancellationToken);

    public async Task<GrantToken?> ResetLink(string by, string? user, CancellationToken cancellationToken) =>
        AccountRules.NormalizeUserName(user) is { } name ? await grants.CreateReset(by, name, cancellationToken) : null;

    /// <summary>The owner role is only ever given by the claim; it cannot be granted or taken here.</summary>
    public async Task<bool> SetRole(string? user, Role role, CancellationToken cancellationToken)
    {
        if (role == Role.Owner || AccountRules.NormalizeUserName(user) is not { } name) return false;
        if (await identity.FindAsync(name) is not { Role: not Role.Owner }) return false;
        return await identity.SetRoleAsync(name, role);
    }

    public async Task<bool> SetDisabled(string? user, bool disabled, CancellationToken cancellationToken) =>
        AccountRules.NormalizeUserName(user) is { } name && await accounts.SetDisabled(name, disabled, cancellationToken);

    /// <summary>Boards whose owner's account is gone.</summary>
    public async Task<IReadOnlyList<OrphanBoard>> Orphans(CancellationToken cancellationToken) =>
        (await context.Boards.AsNoTracking().Where(b => b.Owner == null)
            .Select(b => new OrphanBoard(b.Id, b.Title, b.Updated)).ToListAsync(cancellationToken))
        .OrderByDescending(b => b.Updated).ToList();

    public async Task<IReadOnlyList<AdminBoard>> Boards(CancellationToken cancellationToken) =>
        (await context.Boards.AsNoTracking()
            .Select(b => new AdminBoard(b.Id, b.Title, b.Owner ?? "", b.Seq, b.Updated)).ToListAsync(cancellationToken))
        .OrderByDescending(b => b.Updated).ThenBy(b => b.Id, StringComparer.Ordinal).ToList();

    /// <summary>Gives an orphaned board to someone.</summary>
    public async Task<bool> AssignOwner(string board, string? user, CancellationToken cancellationToken) =>
        AccountRules.NormalizeUserName(user) is { } name && await identity.FindAsync(name) is not null &&
        await context.Boards.Where(b => b.Id == board && b.Owner == null).ExecuteUpdateAsync(set => set.SetProperty(b => b.Owner, name), cancellationToken) == 1;

    /// <summary>Moves a board to another owner.</summary>
    public async Task<bool> SetBoardOwner(string board, string? user, CancellationToken cancellationToken) =>
        AccountRules.NormalizeUserName(user) is { } name && await identity.FindAsync(name) is not null &&
        await context.Boards.Where(b => b.Id == board).ExecuteUpdateAsync(set => set.SetProperty(b => b.Owner, name), cancellationToken) == 1;

    public async Task<bool> DeleteBoard(string board, CancellationToken cancellationToken)
    {
        if (await context.Boards.Where(b => b.Id == board).ExecuteDeleteAsync(cancellationToken) == 0) return false;
        await publisher.Publish(new BoardDeletedEvent(board), cancellationToken);
        return true;
    }

    public async Task<ServerCounts> Counts(CancellationToken cancellationToken) => new(
        (await identity.ListAsync()).Count,
        await context.Boards.CountAsync(cancellationToken),
        await context.Credentials.CountAsync(c => c.Kind == CredentialKind.Device, cancellationToken));

    /// <summary>Every template people shared, in any folder.</summary>
    public async Task<IReadOnlyList<SharedTemplate>> Templates(CancellationToken cancellationToken)
    {
        var rows = await (from template in context.Templates
                          join folder in context.Folders on template.Folder equals folder.Id into folders
                          from folder in folders.DefaultIfEmpty()
                          select new { template.Id, template.Name, template.Author, FolderName = folder == null ? null : folder.Name, template.Updated })
            .AsNoTracking().ToListAsync(cancellationToken);
        return rows.Select(r => new SharedTemplate(r.Id, r.Name, r.Author, r.FolderName, DateTimeOffset.FromUnixTimeMilliseconds(r.Updated)))
            .OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Stops sharing a template, whoever shared it. Copies people added keep their last version.</summary>
    public async Task<bool> RemoveTemplate(string id, CancellationToken cancellationToken) =>
        await context.Templates.Where(t => t.Id == id).ExecuteDeleteAsync(cancellationToken) == 1;

    public bool CanBackup => backup is not null;

    public Task Backup(Stream output, CancellationToken cancellationToken) =>
        backup?.WriteAsync(output, cancellationToken) ?? throw new NotSupportedException("This host backs up its data elsewhere.");

    public async Task<string> ServerName(CancellationToken cancellationToken) =>
        await Get(SettingKeys.ServerName, cancellationToken) is { Length: > 0 } name ? name : Environment.MachineName;

    public async Task<bool> Rename(string? name, CancellationToken cancellationToken)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length is 0 or > AccountRules.MaxServerName || clean.Any(char.IsControl)) return false;
        await Set(SettingKeys.ServerName, clean, cancellationToken);
        return true;
    }

    public Task<string?> ContactEmail(CancellationToken cancellationToken) => Get(SettingKeys.ContactEmail, cancellationToken);

    public async Task<bool> SetContactEmail(string? email, CancellationToken cancellationToken)
    {
        if (AccountRules.CleanEmail(email) is not { } clean) return false;
        await Set(SettingKeys.ContactEmail, clean, cancellationToken);
        return true;
    }

    public Task<string?> SelectedNetworks(CancellationToken cancellationToken) => Get(SettingKeys.Networks, cancellationToken);

    public Task SetSelectedNetworks(string originsCsv, CancellationToken cancellationToken) => Set(SettingKeys.Networks, originsCsv, cancellationToken);

    public Task<string?> DomainName(CancellationToken cancellationToken) => Get(SettingKeys.DomainName, cancellationToken);

    public Task<string?> DomainProvider(CancellationToken cancellationToken) => Get(SettingKeys.DomainProvider, cancellationToken);

    public Task<string?> DesecToken(CancellationToken cancellationToken) => Get(SettingKeys.DesecToken, cancellationToken);

    public async Task SetDomainConfig(string? domain, string? provider, string? desecToken, CancellationToken cancellationToken)
    {
        await Set(SettingKeys.DomainName, (domain ?? "").Trim().ToLowerInvariant(), cancellationToken);
        await Set(SettingKeys.DomainProvider, (provider ?? "").Trim().ToLowerInvariant(), cancellationToken);
        if (desecToken is not null) await Set(SettingKeys.DesecToken, desecToken.Trim(), cancellationToken);
    }

    private Task<string?> Get(string key, CancellationToken cancellationToken) =>
        context.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);

    private async Task Set(string key, string value, CancellationToken cancellationToken)
    {
        var setting = await context.Settings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (setting is null) context.Settings.Add(new Setting { Key = key, Value = value });
        else setting.Value = value;
        await context.SaveChangesAsync(cancellationToken);
    }
}
