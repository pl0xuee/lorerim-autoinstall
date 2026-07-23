using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Lorerim.Gui.Services.Nexus;

/// <summary>
/// Registers this app as the xdg handler for the jackify:// scheme (the Nexus OAuth client
/// "jackify" pins its redirect URI to jackify://oauth/callback, so the scheme is fixed).
/// Re-run on every startup: the AppImage path changes between versions.
///
/// Always call this off the UI thread — it shells out to update-desktop-database, xdg-mime
/// and xdg-settings, and xdg-settings in particular can take seconds on KDE.
/// </summary>
public class ProtocolHandlerRegistrar(LogService log)
{
    public const string Scheme = "jackify";
    private const string DesktopFileName = "lorerim-autoinstall-oauth.desktop";

    /// <summary>Registers the handler on a background thread. Safe to call repeatedly.</summary>
    public Task EnsureRegisteredAsync() => Task.Run(EnsureRegistered);

    private void EnsureRegistered()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        try
        {
            var appsDir = Path.GetFullPath(
                Path.Join(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "..",
                    "share",
                    "applications"
                )
            );
            Directory.CreateDirectory(appsDir);
            var desktopFile = Path.Join(appsDir, DesktopFileName);

            var execPath =
                Environment.GetEnvironmentVariable("APPIMAGE")
                ?? Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine executable path");
            foreach (var c in execPath)
            {
                if (char.IsControl(c))
                {
                    // A newline would terminate the Exec= key and let the rest of the path
                    // inject arbitrary desktop-file keys into a handler any website can fire.
                    log.Append("Executable path contains control characters; not registering the sign-in handler.");
                    return;
                }
            }

            var content = $"""
                [Desktop Entry]
                Type=Application
                Name=LoreRim Autoinstall (OAuth handler)
                Comment=Handles Nexus Mods sign-in callbacks
                Exec={QuoteExecArg(execPath)} %u
                Terminal=false
                NoDisplay=true
                Categories=Game;Utility;
                MimeType=x-scheme-handler/{Scheme};
                """;

            // Only re-register when something actually changed; the xdg calls are the slow part.
            if (File.Exists(desktopFile) && File.ReadAllText(desktopFile) == content)
            {
                return;
            }

            File.WriteAllText(desktopFile, content);
            RunQuiet("update-desktop-database", appsDir);
            RunQuiet("xdg-mime", "default", DesktopFileName, $"x-scheme-handler/{Scheme}");
            RunQuiet("xdg-settings", "set", "default-url-scheme-handler", Scheme, DesktopFileName);
            log.Append($"Registered {Scheme}:// handler at {desktopFile}");
        }
        catch (Exception e)
        {
            log.Append($"Protocol handler registration failed: {e.Message}");
        }
    }

    /// <summary>
    /// Desktop Entry spec quoting: % doubles (field-code expansion), and inside a quoted
    /// argument backslash and double-quote are backslash-escaped.
    /// </summary>
    internal static string QuoteExecArg(string path)
    {
        var escaped = path
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("%", "%%");
        return $"\"{escaped}\"";
    }

    private static void RunQuiet(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
            using var p = Process.Start(psi);
            if (p is null)
            {
                return;
            }
            if (!p.WaitForExit(5_000))
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // xdg tooling missing is non-fatal; the desktop file alone often suffices
        }
    }
}
