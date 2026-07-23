using System;
using System.Collections.Generic;
using System.Linq;

namespace Lorerim.Gui.Services.Steam;

public enum ProtonSuitability
{
    /// <summary>The exact build LoreRim is tested against.</summary>
    Required,

    /// <summary>Same GE-Proton major line as the required build; usually fine.</summary>
    Compatible,

    /// <summary>A GE-Proton build outside the tested line; ENB may misbehave.</summary>
    Untested,

    /// <summary>Known not to work with LoreRim (Proton-CachyOS, Valve Proton).</summary>
    Unsupported,
}

/// <summary>
/// LoreRim pins a specific Proton build for ENB compatibility. Source: Jackify's
/// modlist_proton_requirements.py — "Proton-CachyOS 11 and Valve Proton are known to not
/// work with this list."
/// </summary>
public static class LorerimProton
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
        return ProtonSuitability.Unsupported;
    }

    /// <summary>Ranking key for the Proton list: best choice for LoreRim first.</summary>
    public static int Rank(CompatTool tool) => (int)Evaluate(tool);

    public static string Describe(ProtonSuitability suitability) =>
        suitability switch
        {
            ProtonSuitability.Required => "matches LoreRim's tested build",
            ProtonSuitability.Compatible => $"same line as {RequiredBuild}",
            ProtonSuitability.Untested => $"not the tested build ({RequiredBuild})",
            _ => "not supported by LoreRim",
        };

    /// <summary>Preflight verdict for the set of installed compatibility tools.</summary>
    public static (ProtonSuitability Best, CompatTool? Tool) Best(IEnumerable<CompatTool> tools)
    {
        var best = tools.OrderBy(Rank).FirstOrDefault();
        return best is null ? (ProtonSuitability.Unsupported, null) : (Evaluate(best), best);
    }
}
