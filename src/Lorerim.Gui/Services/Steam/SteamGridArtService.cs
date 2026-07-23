using System;
using System.IO;
using Avalonia.Platform;

namespace Lorerim.Gui.Services.Steam;

/// <summary>
/// Installs the bundled shortcut artwork (Assets/SteamGrid, rendered by
/// scripts/gen-steam-grid.sh) into Steam's per-user grid directory so the GAMMA
/// entry gets library capsules, hero, logo and icon instead of gray placeholders.
/// </summary>
public class SteamGridArtService(LogService log)
{
    private const string AssetBase = "avares://Lorerim.Gui/Assets/SteamGrid/";

    /// <summary>
    /// Writes the shortcut icon to a name that doesn't depend on the appid and
    /// returns its path, for shortcuts.vdf's icon field. Never throws: art is
    /// cosmetic and must not fail the setup pipeline.
    /// </summary>
    public string? InstallIcon(SteamInstallation steam)
    {
        try
        {
            var path = Path.Join(GridDir(steam), "lorerim-icon.png");
            Copy("icon.png", path);
            return path;
        }
        catch (Exception e)
        {
            log.Append($"Could not write shortcut icon: {e.Message}");
            return null;
        }
    }

    /// <summary>Writes capsules/hero/logo keyed by the shortcut's unsigned appid.</summary>
    public void InstallGridArt(SteamInstallation steam, long unsignedAppId)
    {
        try
        {
            var grid = GridDir(steam);
            Copy("landscape.png", Path.Join(grid, $"{unsignedAppId}.png"));
            Copy("portrait.png", Path.Join(grid, $"{unsignedAppId}p.png"));
            Copy("hero.png", Path.Join(grid, $"{unsignedAppId}_hero.png"));
            Copy("logo.png", Path.Join(grid, $"{unsignedAppId}_logo.png"));
            log.Append($"Library artwork installed for appid {unsignedAppId}");
        }
        catch (Exception e)
        {
            log.Append($"Could not write library artwork: {e.Message}");
        }
    }

    private static string GridDir(SteamInstallation steam)
    {
        var dir = Path.Join(steam.UserConfigDir, "grid");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Copy(string asset, string destination)
    {
        using var source = AssetLoader.Open(new Uri(AssetBase + asset));
        using var dest = File.Create(destination);
        source.CopyTo(dest);
    }
}
