using Microsoft.AspNetCore.Identity;
using Penrill.Core.Accounts;

namespace Penrill.Api.Hosting;

internal sealed class IdentityPasswordHashing : IPasswordHashing
{
    private readonly PasswordHasher<string> hasher = new();

    public string Hash(string user, string password) => hasher.HashPassword(user, password);

    public bool Verify(string user, string hash, string password) =>
        hasher.VerifyHashedPassword(user, hash, password) != PasswordVerificationResult.Failed;
}
