using System.Text.RegularExpressions;

namespace Domain.Constants;

public static partial class AccountRules
{
    public const int MinPassword = 10;
    public const int MaxPassword = 256;
    public const int MaxDeviceName = 64;
    public const int MaxServerName = 64;
    public const int MaxEmail = 254;

    /// <summary>The canonical form of a user name: trimmed, lower case, 3 to 32 of a-z, 0-9, _ and -. Null when it is not one.</summary>
    public static string? NormalizeUserName(string? name)
    {
        var user = name?.Trim().ToLowerInvariant();
        return user is not null && UserName().IsMatch(user) ? user : null;
    }

    public static bool IsPassword(string? password) => password is { Length: >= MinPassword and <= MaxPassword };

    /// <summary>A device label without control characters, cut to <see cref="MaxDeviceName"/>.</summary>
    public static string DeviceName(string? name)
    {
        var clean = Clip(name, MaxDeviceName);
        return clean.Length == 0 ? "Unnamed device" : clean;
    }

    public static string Clip(string? text, int max)
    {
        var clean = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > max ? clean[..max] : clean;
    }

    /// <summary>A plausible address (something@domain), trimmed; deliverability is not checked.</summary>
    public static string? CleanEmail(string? email)
    {
        var clean = (email ?? "").Trim();
        var at = clean.IndexOf('@');
        return clean.Length <= MaxEmail && at > 0 && at == clean.LastIndexOf('@') && clean.IndexOf('.', at) > at + 1 &&
            !clean.EndsWith('.') && !clean.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ? clean : null;
    }

    [GeneratedRegex(@"\A[a-z0-9_-]{3,32}\z")]
    private static partial Regex UserName();
}
