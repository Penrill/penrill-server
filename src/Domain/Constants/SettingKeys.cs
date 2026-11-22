namespace Penrill.Domain.Constants;

public static class SettingKeys
{
    public const string ServerName = "server.name";
    /// <summary>Who runs the server. Kept for notices about it (updates, security), not used to sign in.</summary>
    public const string ContactEmail = "server.email";
    /// <summary>Comma-separated origins (LAN, Tailscale, mDNS, custom domain) enabled for connect and invite links.</summary>
    public const string Networks = "server.networks";
    /// <summary>Configured public domain hostname (e.g. myboard.dedyn.io).</summary>
    public const string DomainName = "server.domain.name";
    /// <summary>Public domain provider ("desec" or "custom").</summary>
    public const string DomainProvider = "server.domain.provider";
    /// <summary>deSEC API token used for automatic DDNS updates and ACME DNS-01 renewals.</summary>
    public const string DesecToken = "server.domain.desec_token";
}
