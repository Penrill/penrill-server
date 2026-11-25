namespace Application.Common.Models;

public sealed class AccountOptions
{
    /// <summary>Anyone who reaches the host may create an account. When false, new accounts require an invite.</summary>
    public bool OpenRegistration { get; set; }
}
