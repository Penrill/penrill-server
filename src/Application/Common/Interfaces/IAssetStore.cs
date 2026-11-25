namespace Application.Common.Interfaces;

/// <summary>Image bytes by their SHA-256, shared by every board that uses them. A file folder on a server, blob storage in the cloud.</summary>
public interface IAssetStore
{
    Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken);

    /// <summary>Stores the content once; saving the same hash again does nothing.</summary>
    Task SaveAsync(string hash, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    Task<Stream?> OpenReadAsync(string hash, CancellationToken cancellationToken);
}
