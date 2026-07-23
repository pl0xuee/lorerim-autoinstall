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
        try
        {
            var psi = new ProcessStartInfo { FileName = "xdg-open", UseShellExecute = false };
            psi.ArgumentList.Add(uri.AbsoluteUri);
            // AppImage library paths must not leak into the browser process.
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
