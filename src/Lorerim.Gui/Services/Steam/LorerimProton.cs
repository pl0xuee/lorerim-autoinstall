using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lorerim.Gui.Services.Steam;

public enum ProtonSuitability
{
    /// <summary>The exact build LoreRim is tested against.</summary>
    Required,

    /// <summary>Same GE-Proton major line as the required build; usually fine.</summary>
    Compatible,

    /// <summary>A GE-Proton build outside the tested line; ENB may misbehave.</summary>
    Untested,

    /// <summary>
    /// Proton-CachyOS. Documented as not working with LoreRim, but reachable by the fallback
    /// chain when no GE build can run — a wrong Proton beats an install that cannot finish.
    /// </summary>
    LastResort,

    /// <summary>Known not to work with LoreRim (Valve Proton).</summary>
    Unsupported,
}

/// <summary>
/// LoreRim pins a specific Proton build for ENB compatibility. Source: Jackify's
/// modlist_proton_requirements.py — "Proton-CachyOS 11 and Valve Proton are known to not
/// work with this list."
/// </summary>
public static partial class LorerimProton
{
    public const string RequiredBuild = "GE-Proton10-34";
    private const string RequiredLine = "GE-Proton10-";

    public const string Guidance =
        "LoreRim needs GE-Proton10-34 for ENB. Install it with ProtonUp-Qt "
        + "(or from GloriousEggroll's releases). Proton-CachyOS and Valve Proton "
        + "are known not to work with this list.";

    public static ProtonSuitability Evaluate(CompatTool tool)
    {
        var name = tool.InternalName;
        if (name.Equals(RequiredBuild, StringComparison.OrdinalIgnoreCase))
        {
            return ProtonSuitability.Required;
        }
        if (name.StartsWith(RequiredLine, StringComparison.OrdinalIgnoreCase))
        {
            return ProtonSuitability.Compatible;
        }
        if (name.StartsWith("GE-Proton", StringComparison.OrdinalIgnoreCase))
        {
            return ProtonSuitability.Untested;
        }
        // Packaged under several names (proton-cachyos, proton-cachyos-slr), and the internal
        // name is not always the descriptive one, so both are checked.
        if (
            name.Contains("cachyos", StringComparison.OrdinalIgnoreCase)
            || tool.DisplayName.Contains("cachyos", StringComparison.OrdinalIgnoreCase)
        )
        {
            return ProtonSuitability.LastResort;
        }
        return ProtonSuitability.Unsupported;
    }

    /// <summary>Ranking key for the Proton list: best choice for LoreRim first.</summary>
    public static int Rank(CompatTool tool) => (int)Evaluate(tool);

    /// <summary>
    /// Best choice for LoreRim first, and within one suitability tier the newest build first,
    /// so a fallback lands on the newest usable build rather than an arbitrary one.
    /// </summary>
    public static IEnumerable<CompatTool> Order(IEnumerable<CompatTool> tools) =>
        tools
            .OrderBy(Rank)
            .ThenByDescending(
                t => VersionKey(t.DisplayName + " " + t.InternalName),
                VersionComparer.Instance
            );

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

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberRx();

    public static string Describe(ProtonSuitability suitability) =>
        suitability switch
        {
            ProtonSuitability.Required => "matches LoreRim's tested build",
            ProtonSuitability.Compatible => $"same line as {RequiredBuild}",
            ProtonSuitability.Untested => $"not the tested build ({RequiredBuild})",
            ProtonSuitability.LastResort =>
                "not supported by LoreRim, used only as a fallback so the install can finish",
            _ => "not supported by LoreRim",
        };

    /// <summary>Outcome of picking a Proton build for this machine.</summary>
    public sealed record ProtonSelection(
        CompatTool? Tool,
        ProtonSuitability Suitability,
        CompatTool? SubstitutedFor
    );

    /// <summary>
    /// Picks the Proton build to install with. A build whose Steam Linux Runtime is absent
    /// cannot run — protontricks refuses and the install dies after the engine phase — so
    /// usability is filtered before ranking rather than discovered at launch.
    /// </summary>
    public static ProtonSelection Select(
        IEnumerable<CompatTool> tools,
        Func<int, bool> runtimeInstalled,
        string? pinned
    )
    {
        var ranked = Order(tools).ToList();
        var wanted =
            string.IsNullOrWhiteSpace(pinned)
                ? ranked.FirstOrDefault()
                : ranked.FirstOrDefault(t =>
                    t.InternalName.Equals(pinned, StringComparison.OrdinalIgnoreCase)
                );

        var bestUsable = ranked.FirstOrDefault(t => IsUsable(t, runtimeInstalled));

        // A pin is honoured when it can run, except against the one hard compatibility rule
        // this app exists to enforce: a build known not to work loses to one that does.
        var honourWanted =
            wanted is not null
            && IsUsable(wanted, runtimeInstalled)
            && !(
                Evaluate(wanted) == ProtonSuitability.Unsupported
                && bestUsable is not null
                && Evaluate(bestUsable) != ProtonSuitability.Unsupported
            );

        var chosen = honourWanted ? wanted : bestUsable;

        if (chosen is null)
        {
            return new ProtonSelection(null, ProtonSuitability.Unsupported, wanted);
        }
        return new ProtonSelection(
            chosen,
            Evaluate(chosen),
            ReferenceEquals(chosen, wanted) ? null : wanted
        );
    }

    /// <summary>
    /// Why a build was passed over. The two causes need different words from the user: a
    /// missing runtime is fixable by installing it, while a compatibility override is a rule.
    /// </summary>
    public static string SubstitutionReason(
        CompatTool replaced,
        Func<int, bool> runtimeInstalled
    ) =>
        replaced.RequiredRuntimeAppId is { } appId && !runtimeInstalled(appId)
            ? $"needs {SteamRuntimeCatalog.Describe(appId)}, which is not installed"
            : "is known not to work with LoreRim";

    private static bool IsUsable(CompatTool tool, Func<int, bool> runtimeInstalled) =>
        tool.RequiredRuntimeAppId is not { } appId || runtimeInstalled(appId);

}
