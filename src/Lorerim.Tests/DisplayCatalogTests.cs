using System;
using System.IO;
using System.Linq;
using Lorerim.Gui.Services.Display;
using Xunit;

namespace Lorerim.Tests;

/// <summary>
/// Modes come from /sys/class/drm because it reports what a panel *supports*. xrandr reports
/// the current layout instead: on the machine that prompted this feature it showed a 4K panel
/// as 2560x1440 and a rotated one as portrait, so an xrandr-derived list offers the wrong
/// resolutions. xrandr is used only to learn which output is primary.
/// </summary>
public class DisplayCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lorerim-drm-test").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Cleanup trouble must not turn into a spurious test failure.
        }
    }

    [Fact]
    public void ReadsSupportedModesFromConnectedOutputs()
    {
        WriteConnector("card1-DP-1", "connected", ["3440x1440", "3440x1440", "1920x1080"]);

        var outputs = Catalog().Scan();

        var output = Assert.Single(outputs);
        Assert.Equal("DP-1", output.Connector);
        Assert.Equal(new DisplayMode(3440, 1440), output.Native);
    }

    [Fact]
    public void IgnoresDisconnectedOutputs()
    {
        WriteConnector("card1-DP-1", "connected", ["3440x1440"]);
        WriteConnector("card1-HDMI-A-1", "disconnected", []);

        Assert.Single(Catalog().Scan());
    }

    [Fact]
    public void IgnoresConnectedOutputsWithNoModes()
    {
        WriteConnector("card1-DP-1", "connected", ["3440x1440"]);
        WriteConnector("card1-Writeback-1", "connected", []);

        Assert.Single(Catalog().Scan());
    }

    [Fact]
    public void OffersEachResolutionOnceAcrossDisplays()
    {
        WriteConnector("card1-DP-1", "connected", ["3440x1440", "1920x1080"]);
        WriteConnector("card1-DP-2", "connected", ["3840x2160", "1920x1080"]);

        var choices = Catalog().Choices();

        Assert.Equal(1, choices.Count(c => c.Mode == new DisplayMode(1920, 1080)));
        var shared = choices.Single(c => c.Mode == new DisplayMode(1920, 1080));
        Assert.Equal(["DP-1", "DP-2"], shared.Displays.Order());
    }

    [Fact]
    public void MarksThePrimaryOutputFromXrandr()
    {
        WriteConnector("card1-DP-1", "connected", ["3440x1440"]);
        WriteConnector("card1-DP-2", "connected", ["3840x2160"]);

        var outputs = Catalog(Xrandr("DP-2")).Scan();

        Assert.True(outputs.Single(o => o.Connector == "DP-2").IsPrimary);
        Assert.False(outputs.Single(o => o.Connector == "DP-1").IsPrimary);
    }

    [Fact]
    public void FallsBackToTheLargestOutputWhenXrandrIsUnavailable()
    {
        WriteConnector("card1-DP-1", "connected", ["1920x1080"]);
        WriteConnector("card1-DP-2", "connected", ["3840x2160"]);

        var catalog = Catalog(() => null);

        Assert.True(catalog.Scan().Single(o => o.Connector == "DP-2").IsPrimary);
        Assert.True(catalog.PrimaryIsGuess);
    }

    [Fact]
    public void PrimaryFromXrandrIsNotAGuess()
    {
        WriteConnector("card1-DP-1", "connected", ["3440x1440"]);

        var catalog = Catalog(Xrandr("DP-1"));
        _ = catalog.Scan();

        Assert.False(catalog.PrimaryIsGuess);
    }

    [Fact]
    public void ChoicesLeadWithThePrimarysNativeResolution()
    {
        WriteConnector("card1-DP-1", "connected", ["3440x1440", "1920x1080"]);
        WriteConnector("card1-DP-2", "connected", ["3840x2160"]);

        var choices = Catalog(Xrandr("DP-1")).Choices();

        Assert.Equal(new DisplayMode(3440, 1440), choices[0].Mode);
        Assert.True(choices[0].IsPrimaryNative);
    }

    [Fact]
    public void NoDisplaysDetectedYieldsNoChoicesRatherThanThrowing()
    {
        Assert.Empty(Catalog().Scan());
        Assert.Empty(Catalog().Choices());
    }

    private DisplayCatalog Catalog(Func<string?>? xrandr = null) =>
        new(_root, xrandr ?? (() => null));

    private static Func<string?> Xrandr(string primaryConnector) =>
        () =>
            $"Screen 0: minimum 16 x 16, current 5120 x 2880\n"
            + $"{primaryConnector} connected primary 3440x1440+0+1440 (normal) 797mm x 334mm\n";

    private void WriteConnector(string name, string status, string[] modes)
    {
        var dir = Path.Join(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Join(dir, "status"), status + "\n");
        File.WriteAllText(Path.Join(dir, "enabled"), "enabled\n");
        File.WriteAllText(Path.Join(dir, "modes"), string.Join("\n", modes) + (modes.Length > 0 ? "\n" : ""));
    }
}
