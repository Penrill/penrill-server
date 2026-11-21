using Microsoft.Extensions.Options;
using Penrill.Core.Boards;

namespace Penrill.Storage.Sqlite;

public sealed class FileAssetStore(IOptions<SqliteStorageOptions> options) : IAssetStore
{
    private readonly string root = Path.Combine(Path.GetFullPath(options.Value.DataDirectory), "assets");

    public Task<bool> Exists(string hash, CancellationToken cancel) => Task.FromResult(File.Exists(PathOf(hash)));

    public async Task Save(string hash, ReadOnlyMemory<byte> content, CancellationToken cancel)
    {
        var path = PathOf(hash);
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            await file.WriteAsync(content, cancel);
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

    public Stream? OpenRead(string hash)
    {
        var path = PathOf(hash);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous) : null;
    }

    private string PathOf(string hash) =>
        BoardLimits.IsAssetHash(hash) ? Path.Combine(root, hash[..2], hash) : throw new ArgumentException("Not an asset hash.", nameof(hash));
}
