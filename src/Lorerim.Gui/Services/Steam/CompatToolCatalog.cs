using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lorerim.Gui.Services.Steam;

public sealed record CompatTool(
    string InternalName,
    string DisplayName,
    string Directory,
    int? RequiredRuntimeAppId = null
)
{
    public string ProtonBinary => Path.Join(Directory, "proton");
    public override string ToString() => DisplayName;
}

/// <summary>
/// Scans compatibilitytools.d directories for installed Proton builds and ranks them by
/// LoreRim suitability (see <see cref="LorerimProton"/>): the tested GE-Proton10-34 first,
/// then the rest of that GE line, then other GE builds, then everything else.
/// </summary>
public partial class CompatToolCatalog(IReadOnlyList<string>? wellKnownDirs = null)
{
    private static readonly string[] DefaultDirs =
    [
        Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".steam",
            "root",
            "compatibilitytools.d"
        ),
        "/usr/share/steam/compatibilitytools.d",
    ];

    private readonly IReadOnlyList<string> _wellKnownDirs = wellKnownDirs ?? DefaultDirs;

    public List<CompatTool> Scan(string? steamRoot)
    {
        List<string> dirs = [.. _wellKnownDirs];
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
                        toolDir,
                        ReadRequiredRuntimeAppId(toolDir)
                    )
                );
            }
        }
        return LorerimProton.Order(tools.DistinctBy(t => t.InternalName)).ToList();
    }

    /// <summary>
    /// The Steam Linux Runtime a build must run inside, from its toolmanifest. Anything
    /// unreadable or unrecognised reads as "no requirement": filtering out a build that works
    /// today would be a regression, while missing a requirement is only the status quo.
    /// </summary>
    private static int? ReadRequiredRuntimeAppId(string toolDir)
    {
        var manifest = Path.Join(toolDir, "toolmanifest.vdf");
        if (!File.Exists(manifest))
        {
            return null;
        }
        string text;
        try
        {
            text = File.ReadAllText(manifest);
        }
        catch (IOException)
        {
            return null;
        }
        var match = RequiredRuntimeRx().Match(text);
        return match.Success && int.TryParse(match.Groups["id"].Value, out var appId)
            ? appId
            : null;
    }

    [GeneratedRegex("\"compat_tools\"\\s*\\{\\s*\"(?<name>[^\"]+)\"", RegexOptions.Singleline)]
    private static partial Regex InternalNameRx();

    [GeneratedRegex("\"display_name\"\\s+\"(?<dn>[^\"]+)\"")]
    private static partial Regex DisplayNameRx();

    [GeneratedRegex("\"require_tool_appid\"\\s+\"(?<id>\\d+)\"")]
    private static partial Regex RequiredRuntimeRx();
}
