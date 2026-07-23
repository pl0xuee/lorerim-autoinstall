using Lorerim.Gui.Services.Steam;
using Xunit;

namespace Lorerim.Tests;

public class LorerimProtonTests
{
    private static CompatTool Tool(string internalName) =>
        new(internalName, internalName, $"/tools/{internalName}");

    private static CompatTool Tool(string internalName, int requiredRuntimeAppId) =>
        new(internalName, internalName, $"/tools/{internalName}", requiredRuntimeAppId);

    /// <summary>Steam Linux Runtime 4.0 installed, sniper absent — the 2026-07-23 failure.</summary>
    private static bool Slr4Only(int appId) => appId == 4183110;

    [Fact]
    public void TestedBuildIsRequired()
    {
        Assert.Equal(ProtonSuitability.Required, LorerimProton.Evaluate(Tool("GE-Proton10-34")));
    }

    [Fact]
    public void SameGeLineIsCompatible()
    {
        Assert.Equal(ProtonSuitability.Compatible, LorerimProton.Evaluate(Tool("GE-Proton10-12")));
    }

    [Fact]
    public void OtherGeLineIsUntested()
    {
        Assert.Equal(ProtonSuitability.Untested, LorerimProton.Evaluate(Tool("GE-Proton11-1")));
    }

    [Fact]
    public void CachyosIsALastResort()
    {
        // Reachable by the fallback chain, but ranked below every GE build.
        Assert.Equal(
            ProtonSuitability.LastResort,
            LorerimProton.Evaluate(Tool("proton-cachyos-slr"))
        );
    }

    [Fact]
    public void ValveProtonIsUnsupported()
    {
        Assert.Equal(
            ProtonSuitability.Unsupported,
            LorerimProton.Evaluate(Tool("proton_experimental"))
        );
    }

    [Fact]
    public void TestedBuildWinsOverNewerGe()
    {
        // The catalog sorts newest-first within a rank, so a newer GE-Proton11 must still
        // lose to the build LoreRim is actually tested against.
        var selection = LorerimProton.Select(
            [Tool("GE-Proton11-1"), Tool("proton-cachyos-slr"), Tool("GE-Proton10-34")],
            _ => true,
            pinned: null
        );
        Assert.Equal(ProtonSuitability.Required, selection.Suitability);
        Assert.Equal("GE-Proton10-34", selection.Tool!.InternalName);
    }

    [Fact]
    public void ReportsBestAvailableWhenTestedBuildMissing()
    {
        var selection = LorerimProton.Select(
            [Tool("proton-cachyos-slr"), Tool("GE-Proton11-1")],
            _ => true,
            pinned: null
        );
        Assert.Equal(ProtonSuitability.Untested, selection.Suitability);
        Assert.Equal("GE-Proton11-1", selection.Tool!.InternalName);
    }

    [Fact]
    public void TestedBuildWithMissingRuntimeFallsBackToAUsableBuild()
    {
        // GE-Proton10-34 is installed but needs sniper (1628350), which is not. Picking it
        // anyway is what killed a 300 GB install at the protontricks step.
        var selection = LorerimProton.Select(
            [
                Tool("GE-Proton10-34", 1628350),
                Tool("GE-Proton11-1", 4183110),
                Tool("proton-cachyos-slr", 4183110),
            ],
            Slr4Only,
            pinned: null
        );

        Assert.Equal("GE-Proton11-1", selection.Tool!.InternalName);
        Assert.Equal("GE-Proton10-34", selection.SubstitutedFor!.InternalName);
    }

    [Fact]
    public void FallbackPrefersTheNewestUsableGeBuild()
    {
        var selection = LorerimProton.Select(
            [
                Tool("GE-Proton10-34", 1628350),
                Tool("GE-Proton11-1", 4183110),
                Tool("GE-Proton11-20", 4183110),
            ],
            Slr4Only,
            pinned: null
        );

        Assert.Equal("GE-Proton11-20", selection.Tool!.InternalName);
    }

    [Fact]
    public void FallsBackToCachyosWhenNoGeBuildIsUsable()
    {
        var selection = LorerimProton.Select(
            [Tool("GE-Proton10-34", 1628350), Tool("proton-cachyos-slr", 4183110)],
            Slr4Only,
            pinned: null
        );

        Assert.Equal("proton-cachyos-slr", selection.Tool!.InternalName);
        Assert.Equal(ProtonSuitability.LastResort, selection.Suitability);
    }

    [Fact]
    public void FallsThroughToAnyUsableToolWhenNothingBetterExists()
    {
        var selection = LorerimProton.Select(
            [Tool("GE-Proton10-34", 1628350), Tool("proton_experimental", 4183110)],
            Slr4Only,
            pinned: null
        );

        Assert.Equal("proton_experimental", selection.Tool!.InternalName);
        Assert.Equal(ProtonSuitability.Unsupported, selection.Suitability);
    }

    [Fact]
    public void NoSelectionWhenNoToolIsUsable()
    {
        var selection = LorerimProton.Select(
            [Tool("GE-Proton10-34", 1628350)],
            Slr4Only,
            pinned: null
        );

        Assert.Null(selection.Tool);
    }

    [Fact]
    public void UnusablePinFallsThroughAndReportsWhatItReplaced()
    {
        var selection = LorerimProton.Select(
            [Tool("GE-Proton10-34", 1628350), Tool("GE-Proton11-1", 4183110)],
            Slr4Only,
            pinned: "GE-Proton10-34"
        );

        Assert.Equal("GE-Proton11-1", selection.Tool!.InternalName);
        Assert.Equal("GE-Proton10-34", selection.SubstitutedFor!.InternalName);
    }

    [Fact]
    public void UsablePinBeatsABetterRankedBuild()
    {
        var selection = LorerimProton.Select(
            [Tool("GE-Proton10-34", 4183110), Tool("GE-Proton11-1", 4183110)],
            Slr4Only,
            pinned: "GE-Proton11-1"
        );

        Assert.Equal("GE-Proton11-1", selection.Tool!.InternalName);
        Assert.Null(selection.SubstitutedFor);
    }

    [Fact]
    public void SameLineBuildBeatsANewerMajorWhenTheTestedBuildIsUnusable()
    {
        // The pin exists for ENB, so staying in the GE-Proton10 line beats jumping a major.
        var selection = LorerimProton.Select(
            [
                Tool("GE-Proton10-34", 1628350),
                Tool("GE-Proton10-33", 4183110),
                Tool("GE-Proton11-20", 4183110),
            ],
            Slr4Only,
            pinned: null
        );

        Assert.Equal("GE-Proton10-33", selection.Tool!.InternalName);
    }

    [Fact]
    public void UnsupportedPinIsOverriddenWhenSomethingBetterCanRun()
    {
        // A pin must not override the one hard compatibility rule this app exists to enforce.
        var selection = LorerimProton.Select(
            [Tool("proton_experimental", 4183110), Tool("GE-Proton11-1", 4183110)],
            Slr4Only,
            pinned: "proton_experimental"
        );

        Assert.Equal("GE-Proton11-1", selection.Tool!.InternalName);
        Assert.Equal("proton_experimental", selection.SubstitutedFor!.InternalName);
    }

    [Fact]
    public void UnsupportedPinStandsWhenNothingBetterCanRun()
    {
        var selection = LorerimProton.Select(
            [Tool("proton_experimental", 4183110), Tool("GE-Proton11-1", 1628350)],
            Slr4Only,
            pinned: "proton_experimental"
        );

        Assert.Equal("proton_experimental", selection.Tool!.InternalName);
        Assert.Null(selection.SubstitutedFor);
    }

    [Fact]
    public void LastResortIsDescribedAsAFallbackNotAsSupported()
    {
        var description = LorerimProton.Describe(ProtonSuitability.LastResort);

        Assert.NotEqual(LorerimProton.Describe(ProtonSuitability.Unsupported), description);
        Assert.Contains("fallback", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolWithoutARuntimeRequirementIsAlwaysUsable()
    {
        var selection = LorerimProton.Select([Tool("GE-Proton10-34")], _ => false, pinned: null);

        Assert.Equal("GE-Proton10-34", selection.Tool!.InternalName);
    }
}
