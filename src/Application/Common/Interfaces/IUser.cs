namespace Application.Common.Interfaces;

/// <summary>Who sent the current request.</summary>
public interface IUser
{
    /// <summary>The user name, which is also the account id.</summary>
    string? Id { get; }

    List<string>? Roles { get; }

    /// <summary>The public id of the credential the request was signed with, when it came with a bearer token.</summary>
    string? CredentialId { get; }
}
