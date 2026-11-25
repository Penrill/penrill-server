using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Application.Common.Interfaces;
using Application.Common.Models;
using Domain.Enums;

namespace Infrastructure.Identity;

public class IdentityService(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IUserClaimsPrincipalFactory<ApplicationUser> userClaimsPrincipalFactory,
    IAuthorizationService authorizationService,
    TimeProvider time) : IIdentityService
{
    /// <summary>Checked when the name is unknown, so a sign-in takes as long whether or not the account exists.</summary>
    private static readonly Lazy<string> UnknownUserHash = new(() => new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), Guid.NewGuid().ToString()));

    public async Task<string?> GetUserNameAsync(string userId) => (await userManager.FindByIdAsync(userId))?.UserName;

    public async Task<bool> IsInRoleAsync(string userId, string role)
    {
        var user = await userManager.FindByIdAsync(userId);
        return user != null && await userManager.IsInRoleAsync(user, role);
    }

    public async Task<bool> AuthorizeAsync(string userId, string policyName)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user == null) return false;
        var principal = await userClaimsPrincipalFactory.CreateAsync(user);
        return (await authorizationService.AuthorizeAsync(principal, policyName)).Succeeded;
    }

    public async Task<(Result Result, string UserId)> CreateUserAsync(string userName, string password, Role role)
    {
        if (await userManager.FindByIdAsync(userName) is not null) return (Result.Failure([AccountErrors.Taken]), "");
        var user = new ApplicationUser { Id = userName, UserName = userName, Created = time.GetUtcNow() };
        var created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            var taken = created.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateUserName) or "DuplicateKey");
            return (Result.Failure([taken ? AccountErrors.Taken : AccountErrors.Invalid]), "");
        }
        await EnsureRoleAsync(role);
        await userManager.AddToRoleAsync(user, role.ToString());
        return (Result.Success(), user.Id);
    }

    public async Task<Result> DeleteUserAsync(string userId)
    {
        var user = await userManager.FindByIdAsync(userId);
        return user != null ? (await userManager.DeleteAsync(user)).ToApplicationResult() : Result.Success();
    }

    public async Task<string?> CheckPasswordAsync(string userName, string password)
    {
        var user = await userManager.FindByIdAsync(userName);
        if (user is null)
        {
            userManager.PasswordHasher.VerifyHashedPassword(new ApplicationUser(), UnknownUserHash.Value, password);
            return null;
        }
        return await userManager.CheckPasswordAsync(user, password) && !user.Disabled ? user.Id : null;
    }

    public async Task<Result> SetPasswordAsync(string userId, string password)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Result.Failure([AccountErrors.Invalid]);
        foreach (var validator in userManager.PasswordValidators)
            if (!(await validator.ValidateAsync(userManager, user, password)).Succeeded) return Result.Failure([AccountErrors.Invalid]);
        user.PasswordHash = userManager.PasswordHasher.HashPassword(user, password);
        await userManager.UpdateSecurityStampAsync(user);
        return Result.Success();
    }

    public async Task<AccountInfo?> FindAsync(string userId)
    {
        var user = await userManager.FindByIdAsync(userId);
        return user is null ? null : new AccountInfo(user.Id, await RoleOfAsync(user), user.Disabled, user.Created);
    }

    public async Task<IReadOnlyList<AccountInfo>> ListAsync()
    {
        var users = await userManager.Users.AsNoTracking().ToListAsync();
        var accounts = new List<AccountInfo>(users.Count);
        foreach (var user in users) accounts.Add(new AccountInfo(user.Id, await RoleOfAsync(user), user.Disabled, user.Created));
        return accounts.OrderByDescending(a => a.Role).ThenBy(a => a.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<bool> SetRoleAsync(string userId, Role role)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return false;
        await userManager.RemoveFromRolesAsync(user, await userManager.GetRolesAsync(user));
        await EnsureRoleAsync(role);
        return (await userManager.AddToRoleAsync(user, role.ToString())).Succeeded;
    }

    public async Task<bool> SetDisabledAsync(string userId, bool disabled)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return false;
        user.Disabled = disabled;
        return (await userManager.UpdateAsync(user)).Succeeded;
    }

    public async Task<bool> HasOwnerAsync() => (await userManager.GetUsersInRoleAsync(nameof(Role.Owner))).Count > 0;

    private async Task<Role> RoleOfAsync(ApplicationUser user)
    {
        var roles = await userManager.GetRolesAsync(user);
        return roles.Select(name => Enum.TryParse<Role>(name, out var role) ? role : Role.Guest).DefaultIfEmpty(Role.Member).Max();
    }

    private async Task EnsureRoleAsync(Role role)
    {
        if (!await roleManager.RoleExistsAsync(role.ToString())) await roleManager.CreateAsync(new IdentityRole(role.ToString()));
    }
}
