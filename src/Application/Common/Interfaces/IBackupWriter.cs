namespace Penrill.Application.Common.Interfaces;

/// <summary>Writes a consistent copy of all data as a zip archive. Hosts that back up elsewhere (managed databases) do not register one.</summary>
public interface IBackupWriter
{
    Task WriteAsync(Stream output, CancellationToken cancellationToken);
}
