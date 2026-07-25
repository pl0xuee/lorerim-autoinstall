using System;
using System.Diagnostics;

namespace Lorerim.Gui.Services;

/// <summary>
/// Opens URLs from untrusted sources (engine stdout, remote APIs) in the browser.
/// Restricted to http/https: UseShellExecute/xdg-open would otherwise dispatch any
/// registered URI scheme (steam://, file://, third-party handlers) on one click.
/// </summary>
public static class SafeUrl
{
    public static bool TryOpenInBrowser(string? url)
    {
        if (
            !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        )
        {
            return false;
        }
        return Open(uri.AbsoluteUri);
    }

    /// <summary>
    /// Opens a local file or folder in the desktop's default handler. Only for paths the app
    /// itself owns and constructs (its log, its settings directory) — never a path that came
    /// from engine output or a remote response, which must go through
    /// <see cref="TryOpenInBrowser"/> and its scheme allow-list instead.
    /// </summary>
    public static bool TryOpenLocalPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && System.IO.Path.IsPathRooted(path) && Open(path);

    private static bool Open(string target)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = "xdg-open", UseShellExecute = false };
            psi.ArgumentList.Add(target);
            // AppImage library paths must not leak into the spawned process.
            foreach (var v in (string[])["LD_LIBRARY_PATH", "APPIMAGE", "APPDIR", "ARGV0", "OWD"])
            {
                psi.Environment.Remove(v);
            }
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
