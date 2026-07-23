using System;
using System.Collections.Generic;
using Lorerim.Gui.Services;
using Lorerim.Gui.Services.Steam;
using Xunit;

namespace Lorerim.Tests;

/// <summary>
/// Preflight is where an unrunnable Proton has to surface. Learning it after the engine has
/// laid down 300 GB is the failure this check exists to prevent.
/// </summary>
public class PreflightProtonTests
{
    private static CompatTool Tool(string internalName, int? runtimeAppId = null) =>
        new(internalName, internalName, $"/tools/{internalName}", runtimeAppId);

    private static bool Slr4Only(int appId) => appId == 4183110;

    [Fact]
    public void UsableTestedBuildPasses()
    {
        var check = PreflightService.ProtonCheck(
            [Tool("GE-Proton10-34", 4183110)],
            Slr4Only,
            pinned: null
        );

        Assert.Equal(CheckState.Ok, check.State);
    }

    [Fact]
    public void SubstitutionWarnsAndNamesBothBuildsAndTheMissingRuntime()
    {
        var check = PreflightService.ProtonCheck(
            [Tool("GE-Proton10-34", 1628350), Tool("GE-Proton11-1", 4183110)],
            Slr4Only,
            pinned: null
        );

        Assert.Equal(CheckState.Warn, check.State);
        Assert.Contains("GE-Proton10-34", check.Detail);
        Assert.Contains("GE-Proton11-1", check.Detail);
        Assert.Contains("sniper", check.Detail);
    }

    [Fact]
    public void NothingUsableFails()
    {
        var check = PreflightService.ProtonCheck(
            [Tool("GE-Proton10-34", 1628350)],
            Slr4Only,
            pinned: null
        );

        Assert.Equal(CheckState.Fail, check.State);
    }

    [Fact]
    public void NoToolsAtAllFails()
    {
        var check = PreflightService.ProtonCheck([], Slr4Only, pinned: null);

        Assert.Equal(CheckState.Fail, check.State);
    }

    [Fact]
    public void CachyosFallbackWarnsRatherThanFailing()
    {
        var check = PreflightService.ProtonCheck(
            [Tool("GE-Proton10-34", 1628350), Tool("proton-cachyos-slr", 4183110)],
            Slr4Only,
            pinned: null
        );

        Assert.Equal(CheckState.Warn, check.State);
        Assert.Contains("proton-cachyos-slr", check.Detail);
    }

    [Fact]
    public void OverriddenUnsupportedPinIsNotBlamedOnAMissingRuntime()
    {
        // Both builds run on the installed runtime. The substitution happened because the pin
        // is known not to work with LoreRim, so saying its runtime is missing is simply false.
        var check = PreflightService.ProtonCheck(
            [Tool("proton_experimental", 4183110), Tool("GE-Proton11-1", 4183110)],
            Slr4Only,
            pinned: "proton_experimental"
        );

        Assert.Equal(CheckState.Warn, check.State);
        Assert.DoesNotContain("not installed", check.Detail);
    }

    [Theory]
    [InlineData(1628350, "sniper")]
    [InlineData(1391110, "soldier")]
    [InlineData(4183110, "Steam Linux Runtime 4.0")]
    public void KnownRuntimesAreNamed(int appId, string expected)
    {
        Assert.Contains(expected, SteamRuntimeCatalog.Describe(appId));
    }

    [Fact]
    public void UnknownRuntimeFallsBackToItsAppId()
    {
        Assert.Contains("999999", SteamRuntimeCatalog.Describe(999999));
    }
}
