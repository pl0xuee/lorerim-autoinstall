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
    {
        try
        {
            var logDir = Path.Join(Models.AppSettings.AppDataPath, "logs");
            Directory.CreateDirectory(logDir);
            _logFile = Path.Join(logDir, "lorerim-autoinstall.log");
        }
        catch
        {
            _logFile = null;
        }
    }

    public void Append(string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        LineAdded?.Invoke(stamped);
        if (_logFile is null)
        {
            return;
        }
        lock (_fileLock)
        {
            try
            {
                File.AppendAllText(_logFile, $"[{DateTime.Now:yyyy-MM-dd}]{stamped}\n");
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
