using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Application.Common.Interfaces;
using Npgsql;

namespace Infrastructure.Data;

/// <summary>
/// Writes a zip archive containing a consistent PostgreSQL backup alongside all immutable
/// content-addressed image files. Uses <c>pg_dump --format=custom</c> when a compatible client
/// binary is installed, and falls back to a repeatable-read Npgsql JSON export otherwise.
/// </summary>
public sealed class PostgresBackupWriter(ServerData data, string connectionString) : IBackupWriter
{
    public async Task WriteAsync(Stream output, CancellationToken cancellationToken)
    {
        var dumpFile = Path.Combine(Path.GetTempPath(), $"penrill-backup-{Guid.NewGuid():N}.dump");
        try
        {
            var entryName = "penrill.dump";
            if (!await TryRunPgDumpAsync(dumpFile, cancellationToken))
            {
                await WriteLogicalExportAsync(dumpFile, cancellationToken);
                entryName = "penrill.json";
            }

            await using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            await zip.CreateEntryFromFileAsync(dumpFile, entryName, CompressionLevel.NoCompression, cancellationToken);
            if (Directory.Exists(data.Assets))
            {
                foreach (var file in Directory.EnumerateFiles(data.Assets, "*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".tmp", StringComparison.Ordinal)) continue;
                    await zip.CreateEntryFromFileAsync(file, "assets/" + Path.GetRelativePath(data.Assets, file).Replace('\\', '/'), CompressionLevel.NoCompression, cancellationToken);
                }
            }
        }
        finally
        {
            if (File.Exists(dumpFile)) File.Delete(dumpFile);
        }
    }

    private async Task<bool> TryRunPgDumpAsync(string dumpFile, CancellationToken cancellationToken)
    {
        var cs = new NpgsqlConnectionStringBuilder(connectionString);
        var info = new ProcessStartInfo("pg_dump")
        {
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add("--format=custom");
        info.ArgumentList.Add("--file");
        info.ArgumentList.Add(dumpFile);
        if (!string.IsNullOrEmpty(cs.Host)) { info.ArgumentList.Add("--host"); info.ArgumentList.Add(cs.Host); }
        if (cs.Port > 0) { info.ArgumentList.Add("--port"); info.ArgumentList.Add(cs.Port.ToString()); }
        if (!string.IsNullOrEmpty(cs.Username)) { info.ArgumentList.Add("--username"); info.ArgumentList.Add(cs.Username); }
        if (!string.IsNullOrEmpty(cs.Database)) info.ArgumentList.Add(cs.Database);
        if (!string.IsNullOrEmpty(cs.Password)) info.Environment["PGPASSWORD"] = cs.Password;

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Win32Exception)
        {
            return false;
        }
        if (process is null) return false;

        using (process)
        {
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode == 0) return true;
            if (stderr.Contains("server version mismatch", StringComparison.OrdinalIgnoreCase)) return false;
            throw new InvalidOperationException($"pg_dump failed with exit code {process.ExitCode}: {stderr.Trim()}");
        }
    }

    private async Task WriteLogicalExportAsync(string targetFile, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        var tables = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                tables.Add(reader.GetString(0));
        }

        await using var stream = File.Create(targetFile);
        await using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        foreach (var table in tables)
        {
            writer.WritePropertyName(table);
            writer.WriteStartArray();
            var quoted = "\"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            await using var cmd = new NpgsqlCommand($"SELECT * FROM public.{quoted}", conn, tx);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                writer.WriteStartObject();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    writer.WritePropertyName(reader.GetName(i));
                    if (await reader.IsDBNullAsync(i, cancellationToken))
                    {
                        writer.WriteNullValue();
                        continue;
                    }
                    var value = reader.GetValue(i);
                    switch (value)
                    {
                        case byte[] bytes: writer.WriteBase64StringValue(bytes); break;
                        case bool b: writer.WriteBooleanValue(b); break;
                        case int n: writer.WriteNumberValue(n); break;
                        case long l: writer.WriteNumberValue(l); break;
                        case double d: writer.WriteNumberValue(d); break;
                        case DateTimeOffset dto: writer.WriteStringValue(dto); break;
                        case DateTime dt: writer.WriteStringValue(dt); break;
                        default: writer.WriteStringValue(value.ToString()); break;
                    }
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
    }
}
