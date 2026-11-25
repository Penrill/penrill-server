namespace Web.Infrastructure;

/// <summary>Per-address rate limit policies.</summary>
public static class RateLimits
{
    /// <summary>Sign-in, registration, link start and join: 20 a minute.</summary>
    public const string Account = "account";

    /// <summary>A device polls every few seconds while someone approves it, so this is looser than the sign-in limit.</summary>
    public const string LinkPoll = "link-poll";
}
