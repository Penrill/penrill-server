namespace Application.Common.Models;

public sealed record AccountInfo(string Name, Role Role, bool Disabled, DateTimeOffset? Created);

public static class AccountErrors
{
    public const string Taken = "taken";
    public const string Invalid = "invalid";
}
