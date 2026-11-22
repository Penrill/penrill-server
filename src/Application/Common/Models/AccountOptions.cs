namespace Penrill.Application.Common.Models;

public sealed class AccountOptions
{
    /// <summary>Anyone who reaches the host may create an account. Penrill Cloud turns it on; a self-hosted server uses invites.</summary>
    public bool OpenRegistration { get; set; }
}
