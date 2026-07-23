using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Lorerim.Gui.Services;

/// <summary>
/// Crash-safe file writes: stage to a temp file on the same filesystem, flush to disk,
/// then atomically rename over the target. A power loss (which bricked this repo once)
/// leaves either the old file or the new one intact — never a truncated file. Steam's
/// config.vdf/shortcuts.vdf and settings.json are all master-state files where a partial
/// write is silently discarded and loses unrelated data.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(fs))
        {
            writer.Write(contents);
            writer.Flush();
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static async Task WriteAllTextAsync(
        string path,
        string contents,
        CancellationToken ct = default,
        UnixFileMode? unixCreateMode = null
    )
    {
        var tmp = path + ".tmp";
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (unixCreateMode is { } mode && OperatingSystem.IsLinux())
        {
            // Applied at creation so secret-bearing files are never even briefly readable
            // by other users.
            options.UnixCreateMode = mode;
        }
        await using (var fs = new FileStream(tmp, options))
        await using (var writer = new StreamWriter(fs))
        {
            await writer.WriteAsync(contents.AsMemory(), ct);
            await writer.FlushAsync(ct);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
