using Microsoft.AspNetCore.Http;

namespace Penrill.Api.Hosting;

/// <summary>
/// Where people reach this host: the origin put into link pages and connect links, and the
/// certificate pin editors should expect, if any (Strategy). A self-hosted server replaces the
/// default with its address mode; Penrill Cloud keeps the request's own origin.
/// </summary>
public interface IPublicAddress
{
    string Origin(HttpRequest request);

    /// <summary><c>sha256/&lt;base64&gt;</c> of the server's public key when its certificate is not publicly trusted.</summary>
    string? Pin { get; }
}

internal sealed class RequestAddress : IPublicAddress
{
    public string Origin(HttpRequest request) => $"{request.Scheme}://{request.Host}";

    public string? Pin => null;
}
