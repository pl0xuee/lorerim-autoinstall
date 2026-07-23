using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Lorerim.Gui.Services.Steam;

public sealed record SkyrimInstallation(string GameDir, string LibraryRoot, bool FullyInstalled);

/// <summary>
/// Finds Skyrim Special Edition (appid 489830) by scanning every Steam library listed in
/// libraryfolders.vdf for appmanifest_489830.acf.
/// </summary>
public partial class SkyrimLocator(SteamLocator steamLocator)
{
    public const int AppId = 489830;

    public SkyrimInstallation? Locate()
    {
        var steam = steamLocator.Locate();
        if (steam is null)
        {
            return null;
        }
        foreach (var library in EnumerateLibraries(steam.Root))
        {
            var manifest = Path.Join(library, "steamapps", $"appmanifest_{AppId}.acf");
            if (!File.Exists(manifest))
            {
                continue;
            }
            string text;
            try
            {
                text = File.ReadAllText(manifest);
            }
            catch (IOException)
            {
                continue;
            }
            var installDirMatch = InstallDirRx().Match(text);
            if (!installDirMatch.Success)
            {
                continue;
            }
            var gameDir = Path.Join(
                library,
                "steamapps",
                "common",
                installDirMatch.Groups["dir"].Value
            );
            if (!Directory.Exists(gameDir))
            {
                continue;
            }
            // StateFlags 4 = fully installed.
            var stateMatch = StateFlagsRx().Match(text);
            var fullyInstalled =
                stateMatch.Success
                && long.TryParse(stateMatch.Groups["flags"].Value, out var flags)
                && (flags & 4) == 4;
            return new SkyrimInstallation(gameDir, library, fullyInstalled);
        }
        return null;
    }

    private static IEnumerable<string> EnumerateLibraries(string steamRoot)
    {
        yield return steamRoot;
        var vdf = Path.Join(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
        {
            yield break;
        }
        string text;
        try
        {
            text = File.ReadAllText(vdf);
        }
        catch (IOException)
        {
            yield break;
        }
        foreach (Match m in LibraryPathRx().Matches(text))
        {
            var path = m.Groups["path"].Value.Replace("\\\\", "\\").Replace("\\/", "/");
            if (path != steamRoot && Directory.Exists(path))
            {
                yield return path;
            }
        }
    }

    [GeneratedRegex("\"installdir\"\\s+\"(?<dir>[^\"]+)\"")]
    private static partial Regex InstallDirRx();

    [GeneratedRegex("\"StateFlags\"\\s+\"(?<flags>\\d+)\"")]
    private static partial Regex StateFlagsRx();

    [GeneratedRegex("\"path\"\\s+\"(?<path>[^\"]+)\"")]
    private static partial Regex LibraryPathRx();
}
