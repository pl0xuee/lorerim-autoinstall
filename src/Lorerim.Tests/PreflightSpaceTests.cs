using Lorerim.Gui.Services;
using Xunit;

namespace Lorerim.Tests;

public class PreflightSpaceTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void PassesWhenSpaceIsSufficient()
    {
        var check = PreflightService.BuildSpaceCheck("Disk space", 800 * Gb, 600 * Gb, "/home");
        Assert.Equal(CheckState.Ok, check.State);
        Assert.Contains("800 GB free", check.Detail);
    }

    /// <summary>
    /// The case that motivated the combined check: 547 GB free clears each folder's own
    /// threshold but not the two together.
    /// </summary>
    [Fact]
    public void FailsWhenCombinedRequirementExceedsSharedVolume()
    {
        var combined = PreflightService.RequiredDownloadBytes + PreflightService.RequiredInstallBytes;
        Assert.True(547 * Gb >= PreflightService.RequiredDownloadBytes);
        Assert.True(547 * Gb >= PreflightService.RequiredInstallBytes);

        var check = PreflightService.BuildSpaceCheck(
            "Disk space",
            547 * Gb,
            combined,
            "downloads + install share /home"
        );
        Assert.Equal(CheckState.Fail, check.State);
        Assert.Contains("another drive", check.Detail);
    }

    [Theory]
    [InlineData("/home/bob/Games", "/home", true)]
    [InlineData("/home", "/home", true)]
    [InlineData("/mnt/Games2/x", "/mnt/Games", false)] // must not match across a name boundary
    [InlineData("/mnt/Games/x", "/mnt/Games", true)]
    [InlineData("/var/tmp", "/home", false)]
    [InlineData("/home/bob", "/", true)]
    public void MountPrefixMatchingRespectsDirectoryBoundaries(
        string path,
        string root,
        bool expected
    )
    {
        Assert.Equal(expected, PreflightService.IsUnder(path, root));
    }
}
