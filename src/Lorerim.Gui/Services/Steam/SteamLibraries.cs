using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Lorerim.Gui.Services.Steam;

/// <summary>
/// Steam spreads installed apps across the libraries listed in libraryfolders.vdf, so
/// anything looking for an appmanifest has to walk all of them, not just the Steam root.
/// </summary>
public static partial class SteamLibraries
{
    public static IEnumerable<string> Enumerate(string steamRoot)
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

    [GeneratedRegex("\"path\"\\s+\"(?<path>[^\"]+)\"")]
    private static partial Regex LibraryPathRx();
}
