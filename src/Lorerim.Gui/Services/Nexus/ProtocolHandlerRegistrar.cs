using System;
using System.Diagnostics;
using System.IO;

namespace Lorerim.Gui.Services.Nexus;

/// <summary>
/// Registers this app as the xdg handler for the jackify:// scheme (the Nexus OAuth client
/// "jackify" pins its redirect URI to jackify://oauth/callback, so the scheme is fixed).
/// Re-run on every startup: the AppImage path changes between versions.
/// </summary>
public class ProtocolHandlerRegistrar(LogService log)
{
    public const string Scheme = "jackify";
    private const string DesktopFileName = "lorerim-autoinstall-oauth.desktop";

    public void EnsureRegistered()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        try
        {
            var appsDir = Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "..",
                "share",
                "applications"
            );
            appsDir = Path.GetFullPath(appsDir);
            Directory.CreateDirectory(appsDir);
            var desktopFile = Path.Join(appsDir, DesktopFileName);

            var execPath =
                Environment.GetEnvironmentVariable("APPIMAGE")
                ?? Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine executable path");

            var content = $"""
                [Desktop Entry]
                Type=Application
                Name=LoreRim Autoinstall (OAuth handler)
                Comment=Handles Nexus Mods sign-in callbacks
                Exec="{execPath}" %u
                Terminal=false
                NoDisplay=true
                Categories=Game;Utility;
                MimeType=x-scheme-handler/{Scheme};
                """;
            if (!File.Exists(desktopFile) || File.ReadAllText(desktopFile) != content)
            {
                File.WriteAllText(desktopFile, content);
                log.Append($"Registered {Scheme}:// handler at {desktopFile}");
            }

            RunQuiet("update-desktop-database", appsDir);
            RunQuiet("xdg-mime", "default", DesktopFileName, $"x-scheme-handler/{Scheme}");
            RunQuiet(
                "xdg-settings",
                "set",
                "default-url-scheme-handler",
                Scheme,
                DesktopFileName
            );
        }
        catch (Exception e)
        {
            log.Append($"Protocol handler registration failed: {e.Message}");
        }
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
            p?.WaitForExit(10_000);
        }
        catch
        {
            // xdg tooling missing is non-fatal; mimeapps association may still work
        }
    }
}
