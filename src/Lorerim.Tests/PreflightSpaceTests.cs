using System;
using System.IO;
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

    /// <summary>
    /// Updating an existing install needs room for what changed, not for a second copy of
    /// the modlist. Failing here pushed a user into deleting the very install they were
    /// updating, which turned a quick diff into a full re-extract.
    /// </summary>
    [Fact]
    public void ShortfallOnlyWarnsWhenTheInstallAlreadyExists()
    {
        var check = PreflightService.BuildSpaceCheck(
            "Disk space",
            375 * Gb,
            600 * Gb,
            "/mnt/Games",
            existingInstall: true
        );

        Assert.Equal(CheckState.Warn, check.State);
        Assert.Contains("only downloads what changed", check.Detail);
    }

    [Fact]
    public void ShortfallStillFailsForAFreshInstall()
    {
        var check = PreflightService.BuildSpaceCheck(
            "Disk space",
            375 * Gb,
            600 * Gb,
            "/mnt/Games",
            existingInstall: false
        );

        Assert.Equal(CheckState.Fail, check.State);
    }

    [Fact]
    public void AGenuinelyFullDiskStillFailsEvenWithAnExistingInstall()
    {
        // Relaxing the check must not mean "never stop" — an update still has to write.
        var check = PreflightService.BuildSpaceCheck(
            "Disk space",
            5 * Gb,
            600 * Gb,
            "/mnt/Games",
            existingInstall: true
        );

        Assert.Equal(CheckState.Fail, check.State);
    }

    [Fact]
    public void AmpleSpaceStaysOkWithAnExistingInstall()
    {
        var check = PreflightService.BuildSpaceCheck(
            "Disk space",
            800 * Gb,
            600 * Gb,
            "/mnt/Games",
            existingInstall: true
        );

        Assert.Equal(CheckState.Ok, check.State);
    }

    [Fact]
    public void AnEmptyOrMissingFolderIsNotAnExistingInstall()
    {
        using var temp = new TempDir();

        Assert.False(PreflightService.HasExistingInstall(Path.Join(temp.Path, "nope")));
        Assert.False(PreflightService.HasExistingInstall(temp.Path));
    }

    [Fact]
    public void AFolderWithModOrganizerIsAnExistingInstall()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Join(temp.Path, "ModOrganizer.exe"), "");

        Assert.True(PreflightService.HasExistingInstall(temp.Path));
    }

    [Fact]
    public void APopulatedModsFolderIsAnExistingInstall()
    {
        // Mid-run the engine deletes and rewrites files at the root, so the mods tree is the
        // more dependable signal that an install is already on disk.
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Join(temp.Path, "mods", "Some Mod"));

        Assert.True(PreflightService.HasExistingInstall(temp.Path));
    }

    [Fact]
    public void AnEmptyModsFolderIsNotAnExistingInstall()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Join(temp.Path, "mods"));

        Assert.False(PreflightService.HasExistingInstall(temp.Path));
    }

    [Fact]
    public void DownloadsCountAsPresentOnlyWhenTheFolderHasFiles()
    {
        using var temp = new TempDir();
        Assert.False(PreflightService.HasExistingDownloads(temp.Path));

        File.WriteAllText(Path.Join(temp.Path, "SomeMod-1234.7z"), "");
        Assert.True(PreflightService.HasExistingDownloads(temp.Path));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("lorerim-preflight").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Cleanup trouble must not turn into a spurious test failure.
            }
        }
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
