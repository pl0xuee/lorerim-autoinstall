using System.Linq;
using Lorerim.Gui.Services.Display;
using Lorerim.Gui.ViewModels;
using Xunit;

namespace Lorerim.Tests;

public class ResolutionOptionTests
{
    private static ResolutionChoice Choice(int w, int h, bool primaryNative, params string[] displays) =>
        new(new DisplayMode(w, h), displays, primaryNative);

    [Fact]
    public void LeaveAloneIsAlwaysTheFirstEntry()
    {
        var options = ResolutionOption.Build([Choice(3440, 1440, true, "DP-1")], stored: null);

        Assert.Null(options[0].Value);
        Assert.Equal(options[0], options.First(o => o.Value is null));
    }

    [Fact]
    public void NoStoredPreferenceSelectsLeaveAlone()
    {
        var options = ResolutionOption.Build([Choice(3440, 1440, true, "DP-1")], stored: null);

        Assert.Null(ResolutionOption.Select(options, stored: null).Value);
    }

    [Fact]
    public void LabelsNameTheDisplaysOfferingTheResolution()
    {
        var options = ResolutionOption.Build([Choice(1920, 1080, false, "DP-1", "DP-2")], stored: null);

        Assert.Contains("DP-1, DP-2", options.Single(o => o.Value == "1920x1080").Label);
    }

    [Fact]
    public void ThePrimarysNativeModeIsLabelledAsSuch()
    {
        var options = ResolutionOption.Build([Choice(3440, 1440, true, "DP-1")], stored: null);

        Assert.Contains("primary", options.Single(o => o.Value == "3440x1440").Label);
    }

    [Fact]
    public void AStoredResolutionNoDisplayOffersIsStillSelectable()
    {
        // Unplugging the monitor a resolution was chosen for must not silently rewrite the
        // user's setting to "leave alone" the next time the picker is built.
        var options = ResolutionOption.Build([Choice(1920, 1080, true, "DP-1")], stored: "3440x1440");

        var stored = options.Single(o => o.Value == "3440x1440");
        Assert.Contains("not offered", stored.Label);
        Assert.Equal(stored, ResolutionOption.Select(options, "3440x1440"));
    }

    [Fact]
    public void AStoredResolutionThatIsOfferedIsNotDuplicated()
    {
        var options = ResolutionOption.Build([Choice(3440, 1440, true, "DP-1")], stored: "3440x1440");

        Assert.Single(options, o => o.Value == "3440x1440");
    }
}
