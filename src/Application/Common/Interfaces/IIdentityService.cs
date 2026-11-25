using Application.Common.Models;

namespace Application.Common.Interfaces;

/// <summary>
/// Accounts and their passwords and roles. The user name is the account id: boards, folders and
/// templates refer to people by it.
/// </summary>
public interface IIdentityService
{
    Task<string?> GetUserNameAsync(string userId);

    Task<bool> IsInRoleAsync(string userId, string role);

    Task<bool> AuthorizeAsync(string userId, string policyName);

    /// <summary>Creates an account. Fails with <see cref="AccountErrors.Taken"/> or <see cref="AccountErrors.Invalid"/>.</summary>
    Task<(Result Result, string UserId)> CreateUserAsync(string userName, string password, Role role);

    Task<Result> DeleteUserAsync(string userId);

    /// <summary>The account id when the password is right and the account is enabled; null otherwise, after the same work either way.</summary>
    Task<string?> CheckPasswordAsync(string userName, string password);

    Task<Result> SetPasswordAsync(string userId, string password);

    Task<AccountInfo?> FindAsync(string userId);

    /// <summary>Every account, owners and admins first.</summary>
    Task<IReadOnlyList<AccountInfo>> ListAsync();

    Task<bool> SetRoleAsync(string userId, Role role);

    Task<bool> SetDisabledAsync(string userId, bool disabled);

    Task<bool> HasOwnerAsync();
}
