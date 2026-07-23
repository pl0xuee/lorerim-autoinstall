using System;
using System.IO;
using Lorerim.Gui.Services.Steam;
using Xunit;

namespace Lorerim.Tests;

public class LaunchOptionsTests
{
    [Fact]
    public void HomePathsNeedNoMounts()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var options = SteamIntegrationService.BuildLaunchOptions(
            [Path.Join(home, "Games", "LoreRim"), Path.Join(home, "Games", "LoreRim-downloads")]
        );
        Assert.Equal("%command%", options);
    }

    [Fact]
    public void ForeignMountsAreListed()
    {
        var options = SteamIntegrationService.BuildLaunchOptions(
            ["/mnt/ssd/LoreRim", "/mnt/ssd/LoreRim-downloads"]
        );
        Assert.Equal(
            "STEAM_COMPAT_MOUNTS=\"/mnt/ssd/LoreRim:/mnt/ssd/LoreRim-downloads\" %command%",
            options
        );
    }
}
