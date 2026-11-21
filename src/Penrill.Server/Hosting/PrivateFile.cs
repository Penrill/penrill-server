using System.Text;

namespace Penrill.Server.Hosting;

/// <summary>Files only the service account (and root) may read: the TLS key and the setup link.</summary>
internal static class PrivateFile
{
    public static void Write(string path, string text)
    {
        File.Delete(path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new FileStream(path, options);
        file.Write(Encoding.UTF8.GetBytes(text));
        file.Flush(flushToDisk: true);
    }
}
