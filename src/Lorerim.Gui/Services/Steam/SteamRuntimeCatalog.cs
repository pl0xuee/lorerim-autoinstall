using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Lorerim.Gui.Services.Steam;

/// <summary>
/// Answers whether a Steam Linux Runtime is installed. Proton does not run on the host: each
/// build executes inside the runtime its toolmanifest pins, so a missing runtime makes the
/// build unusable no matter how suitable it otherwise looks.
/// </summary>
public partial class SteamRuntimeCatalog
{
    /// <summary>Readable name for a runtime appid, so messages can say what is missing.</summary>
    public static string Describe(int appId) =>
        appId switch
        {
            1391110 => "Steam Linux Runtime 2.0 (soldier)",
            1628350 => "Steam Linux Runtime 3.0 (sniper)",
            4183110 => "Steam Linux Runtime 4.0",
            _ => $"Steam Runtime appid {appId}",
        };

    /// <summary>
    /// A predicate over runtime appids for one Steam root. Without a root nothing can be
    /// known, so everything reads as available and Proton selection behaves as it always has.
    /// </summary>
    public Func<int, bool> AvailabilityFor(string? steamRoot)
    {
        if (steamRoot is null)
        {
            return _ => true;
        }
        var cache = new Dictionary<int, bool>();
        return appId =>
        {
            if (!cache.TryGetValue(appId, out var installed))
            {
                installed = IsInstalled(steamRoot, appId);
                cache[appId] = installed;
            }
            return installed;
        };
    }

    private static bool IsInstalled(string steamRoot, int appId)
    {
        foreach (var library in SteamLibraries.Enumerate(steamRoot))
        {
            var manifest = Path.Join(library, "steamapps", $"appmanifest_{appId}.acf");
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
            // StateFlags 4 = fully installed. A runtime still downloading cannot host a
            // Proton launch, so it counts as absent.
            var match = StateFlagsRx().Match(text);
            if (
                match.Success
                && long.TryParse(match.Groups["flags"].Value, out var flags)
                && (flags & 4) == 4
            )
            {
                return true;
            }
        }
        return false;
    }

    [GeneratedRegex("\"StateFlags\"\\s+\"(?<flags>\\d+)\"")]
    private static partial Regex StateFlagsRx();
}
