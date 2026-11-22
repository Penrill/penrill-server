using Penrill.Application.Common.Interfaces;
using Penrill.Domain.Constants;

namespace Penrill.Infrastructure.Assets;

public sealed class FileAssetOptions
{
    /// <summary>The folder images are kept in, as <c>&lt;first two hex digits&gt;/&lt;sha256&gt;</c>.</summary>
    public string Directory { get; set; } = "";
}

/// <summary>Images as immutable files named by their hash. A write goes to a temporary file first, so a power cut cannot leave half an image.</summary>
public sealed class FileAssetStore(FileAssetOptions options) : IAssetStore
{
    private readonly string _root = Path.GetFullPath(options.Directory);

    public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken) => Task.FromResult(File.Exists(PathOf(hash)));

    public async Task SaveAsync(string hash, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var path = PathOf(hash);
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            await file.WriteAsync(content, cancellationToken);
            file.Flush(flushToDisk: true);
        }
        try
        {
            File.Move(temporary, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temporary);
        }
    }

    public Task<Stream?> OpenReadAsync(string hash, CancellationToken cancellationToken)
    {
        var path = PathOf(hash);
        return Task.FromResult<Stream?>(File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous) : null);
    }

    private string PathOf(string hash) =>
        BoardLimits.IsAssetHash(hash) ? Path.Combine(_root, hash[..2], hash) : throw new ArgumentException("Not an asset hash.", nameof(hash));
}
