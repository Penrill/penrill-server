using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Penrill.Application.Common.Interfaces;

namespace Penrill.Server.Data;

/// <summary>
/// <c>VACUUM INTO</c> gives a consistent copy while the server keeps running; images are immutable files named by
/// their hash, so copying them afterwards cannot tear.
/// </summary>
public sealed class SqliteBackupWriter(ServerData data) : IBackupWriter
{
    public async Task WriteAsync(Stream output, CancellationToken cancellationToken)
    {
        var snapshot = Path.Combine(Path.GetTempPath(), $"penrill-backup-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection(data.ConnectionString))
            {
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "VACUUM INTO $path";
                command.Parameters.AddWithValue("$path", snapshot);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            SqliteConnection.ClearAllPools();
            await using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            await zip.CreateEntryFromFileAsync(snapshot, "penrill.db", CompressionLevel.Optimal, cancellationToken);
            if (Directory.Exists(data.Assets))
                foreach (var file in Directory.EnumerateFiles(data.Assets, "*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".tmp", StringComparison.Ordinal)) continue;
                    await zip.CreateEntryFromFileAsync(file, "assets/" + Path.GetRelativePath(data.Assets, file).Replace('\\', '/'), CompressionLevel.NoCompression, cancellationToken);
                }
        }
        finally
        {
            File.Delete(snapshot);
        }
    }
}
