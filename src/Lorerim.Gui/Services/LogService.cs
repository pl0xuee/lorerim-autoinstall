using System;
using System.IO;
using Lorerim.Gui.Models;

namespace Lorerim.Gui.Services;

/// <summary>
/// In-app log sink feeding the collapsible log pane, teed to
/// ~/.config/lorerim-autoinstall/logs/lorerim-autoinstall.log so failed runs survive an app close.
/// Thread-safe; UI marshalling is the subscriber's job.
/// </summary>
public class LogService
{
    public event Action<string>? LineAdded;

    public LogService()
        : this(Path.Join(Models.AppSettings.AppDataPath, "logs")) { }

    /// <summary>
    /// Sinks to <paramref name="logDirectory"/>, or to memory only when it is null. Anything
    /// that is not the running app — tests above all — must pass null: writing to the real
    /// log interleaves lines into the record a user reads to diagnose a failed install.
    /// </summary>
    internal LogService(string? logDirectory)
    {
        if (logDirectory is null)
        {
            _logFile = null;
            return;
        }
        try
        {
            Directory.CreateDirectory(logDirectory);
            _logFile = Path.Join(logDirectory, "lorerim-autoinstall.log");
        }
        catch
        {
            _logFile = null;
        }
    }

    /// <summary>
    /// The file every line is teed to, or null when this sink is memory-only. Surfaced so the
    /// UI can point a user at the full record: the in-app pane keeps only the last 2000 lines,
    /// and a failed install is diagnosed from the file.
    /// </summary>
    public string? LogFilePath => _logFile;

    public void Append(string line)
    {
        // One timestamp for both sinks: reading the clock twice can straddle midnight and
        // stamp the file line with a different day than the one shown in the pane.
        var now = DateTime.Now;
        var stamped = $"[{now:HH:mm:ss}] {line}";
        LineAdded?.Invoke(stamped);
        if (_logFile is null)
        {
            return;
        }
        lock (_fileLock)
        {
            try
            {
                File.AppendAllText(_logFile, $"[{now:yyyy-MM-dd}]{stamped}\n");
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }

    private readonly string? _logFile;
    private readonly object _fileLock = new();
}
