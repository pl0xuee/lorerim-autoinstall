using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lorerim.Gui.Services.Steam;

public sealed record CompatTool(string InternalName, string DisplayName, string Directory)
{
    public string ProtonBinary => Path.Join(Directory, "proton");
    public override string ToString() => DisplayName;
}

/// <summary>
/// Scans compatibilitytools.d directories for installed Proton builds and ranks them by
/// LoreRim suitability (see <see cref="LorerimProton"/>): the tested GE-Proton10-34 first,
/// then the rest of that GE line, then other GE builds, then everything else.
/// </summary>
public partial class CompatToolCatalog
{
    public List<CompatTool> Scan(string? steamRoot)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        List<string> dirs =
        [
            Path.Join(home, ".steam", "root", "compatibilitytools.d"),
            "/usr/share/steam/compatibilitytools.d",
        ];
        if (steamRoot is not null)
        {
            dirs.Add(Path.Join(steamRoot, "compatibilitytools.d"));
        }

        var tools = new List<CompatTool>();
        foreach (var dir in dirs.Distinct().Where(System.IO.Directory.Exists))
        {
            foreach (var toolDir in System.IO.Directory.GetDirectories(dir))
            {
                var vdf = Path.Join(toolDir, "compatibilitytool.vdf");
                if (!File.Exists(vdf) || !File.Exists(Path.Join(toolDir, "proton")))
                {
                    continue;
                }
                var text = File.ReadAllText(vdf);
                // First key inside "compat_tools" is the internal name Steam uses in CompatToolMapping.
                var nameMatch = InternalNameRx().Match(text);
                var displayMatch = DisplayNameRx().Match(text);
                if (!nameMatch.Success)
                {
                    continue;
                }
                tools.Add(
                    new CompatTool(
                        nameMatch.Groups["name"].Value,
                        displayMatch.Success ? displayMatch.Groups["dn"].Value : nameMatch.Groups["name"].Value,
                        toolDir
                    )
                );
            }
        }
        return tools
            .DistinctBy(t => t.InternalName)
            .OrderBy(Rank)
            .ThenByDescending(t => VersionKey(t.DisplayName + " " + t.InternalName), VersionComparer.Instance)
            .ToList();
    }

    public CompatTool? PickBest(List<CompatTool> tools) => tools.FirstOrDefault();

    private static int Rank(CompatTool t) => LorerimProton.Rank(t);

    // long, and clamp on overflow: a date-stamped build (GE-Proton-20250101120000) exceeds
    // int range and would otherwise throw OverflowException, crashing the Steam page.
    private static long[] VersionKey(string s) =>
        NumberRx()
            .Matches(s)
            .Select(m => long.TryParse(m.Value, out var n) ? n : long.MaxValue)
            .ToArray();

    private sealed class VersionComparer : IComparer<long[]>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(long[]? x, long[]? y)
        {
            x ??= [];
            y ??= [];
            for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                var xi = i < x.Length ? x[i] : 0;
                var yi = i < y.Length ? y[i] : 0;
                if (xi != yi)
                {
                    return xi.CompareTo(yi);
                }
            }
            return 0;
        }
    }

    [GeneratedRegex("\"compat_tools\"\\s*\\{\\s*\"(?<name>[^\"]+)\"", RegexOptions.Singleline)]
    private static partial Regex InternalNameRx();

    [GeneratedRegex("\"display_name\"\\s+\"(?<dn>[^\"]+)\"")]
    private static partial Regex DisplayNameRx();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberRx();
}
