using Lorerim.Gui.Services.Steam;
using Xunit;

namespace Lorerim.Tests;

public class LorerimProtonTests
{
    private static CompatTool Tool(string internalName) =>
        new(internalName, internalName, $"/tools/{internalName}");

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

    [Theory]
    [InlineData("proton-cachyos-slr")]
    [InlineData("proton_experimental")]
    public void CachyosAndValveAreUnsupported(string name)
    {
        Assert.Equal(ProtonSuitability.Unsupported, LorerimProton.Evaluate(Tool(name)));
    }

    [Fact]
    public void TestedBuildWinsOverNewerGe()
    {
        // The catalog sorts newest-first within a rank, so a newer GE-Proton11 must still
        // lose to the build LoreRim is actually tested against.
        var (suitability, best) = LorerimProton.Best(
            [Tool("GE-Proton11-1"), Tool("proton-cachyos-slr"), Tool("GE-Proton10-34")]
        );
        Assert.Equal(ProtonSuitability.Required, suitability);
        Assert.Equal("GE-Proton10-34", best!.InternalName);
    }

    [Fact]
    public void ReportsBestAvailableWhenTestedBuildMissing()
    {
        var (suitability, best) = LorerimProton.Best(
            [Tool("proton-cachyos-slr"), Tool("GE-Proton11-1")]
        );
        Assert.Equal(ProtonSuitability.Untested, suitability);
        Assert.Equal("GE-Proton11-1", best!.InternalName);
    }
}
