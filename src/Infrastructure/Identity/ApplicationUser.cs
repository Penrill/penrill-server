using Microsoft.AspNetCore.Identity;

namespace Infrastructure.Identity;

/// <summary>
/// A Penrill account. Its <see cref="IdentityUser.Id"/> is its user name: boards, folders and templates refer to
/// people by it, and the name never changes. The role is one of <see cref="Domain.Enums.Role"/>, held as an Identity role.
/// </summary>
public class ApplicationUser : IdentityUser
{
    public bool Disabled { get; set; }

    public DateTimeOffset Created { get; set; }
}
