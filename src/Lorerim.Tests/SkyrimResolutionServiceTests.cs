using System;
using System.IO;
using System.Text;
using Lorerim.Gui.Services.Modlist;
using Xunit;

namespace Lorerim.Tests;

/// <summary>
/// skyrimprefs.ini is BethINI-generated: UTF-8 with a BOM, CRLF terminators, and keys
/// written as "iSize W =3840". Rewriting the file wholesale would normalise all of that,
/// so the writer replaces digits in place and everything else must survive untouched.
/// </summary>
public class SkyrimResolutionServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lorerim-res-test").FullName;

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
    public void ReadsTheResolutionFromTheActiveProfile()
    {
        WriteInstall(activeProfile: "Ultra", profiles: ["Default", "Ultra"]);

        Assert.Equal((3840, 2160), SkyrimResolutionService.Read(_root));
    }

    [Fact]
    public void WritesEveryProfileNotJustTheActiveOne()
    {
        WriteInstall(activeProfile: "Ultra", profiles: ["Default", "Ultra", "Extreme"]);

        SkyrimResolutionService.Apply(_root, 3440, 1440);

        foreach (var profile in new[] { "Default", "Ultra", "Extreme" })
        {
            var text = File.ReadAllText(PrefsPath(profile));
            Assert.Contains("iSize W =3440", text);
            Assert.Contains("iSize H =1440", text);
        }
    }

    [Fact]
    public void PreservesTheBomAndCrlfTerminators()
    {
        WriteInstall(activeProfile: "Ultra", profiles: ["Ultra"]);

        SkyrimResolutionService.Apply(_root, 2560, 1440);

        var bytes = File.ReadAllBytes(PrefsPath("Ultra"));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\r\n", text);
        Assert.DoesNotContain(text.Replace("\r\n", ""), "\n");
    }

    [Fact]
    public void LeavesEveryOtherLineByteForByte()
    {
        WriteInstall(activeProfile: "Ultra", profiles: ["Ultra"]);
        var before = File.ReadAllLines(PrefsPath("Ultra"));

        SkyrimResolutionService.Apply(_root, 2560, 1440);

        var after = File.ReadAllLines(PrefsPath("Ultra"));
        Assert.Equal(before.Length, after.Length);
        for (var i = 0; i < before.Length; i++)
        {
            if (before[i].StartsWith("iSize", StringComparison.Ordinal))
            {
                continue;
            }
            Assert.Equal(before[i], after[i]);
        }
    }

    [Fact]
    public void InsertsMissingKeysIntoTheDisplaySection()
    {
        Directory.CreateDirectory(Path.Join(_root, "profiles", "Ultra"));
        WriteModOrganizerIni("Ultra");
        WriteBytes(
            PrefsPath("Ultra"),
            "[General]\r\nuGridsToLoad =5\r\n\r\n[Display]\r\nbBorderless =1\r\n"
        );

        SkyrimResolutionService.Apply(_root, 1920, 1080);

        var text = File.ReadAllText(PrefsPath("Ultra"));
        Assert.Contains("iSize W =1920", text);
        Assert.Contains("iSize H =1080", text);
        // Must land under [Display], not appended after an unrelated section.
        var display = text.IndexOf("[Display]", StringComparison.Ordinal);
        Assert.True(text.IndexOf("iSize W", StringComparison.Ordinal) > display);
    }

    [Fact]
    public void ReadReturnsNullWhenThereIsNoInstall()
    {
        Assert.Null(SkyrimResolutionService.Read(Path.Join(_root, "nope")));
    }

    [Theory]
    [InlineData("3440x1440", 3440, 1440)]
    [InlineData("1920X1080", 1920, 1080)]
    [InlineData(" 2560 x 1440 ", 2560, 1440)]
    public void ParsesAStoredResolution(string stored, int width, int height)
    {
        Assert.True(SkyrimResolutionService.TryParse(stored, out var parsed));
        Assert.Equal((width, height), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("native")]
    [InlineData("25640x1440x900")]
    [InlineData("0x1080")]
    public void RejectsAnythingThatIsNotAResolution(string? stored)
    {
        // The shipped LoreRim ini contains a "25640x1440" typo, so a malformed value must
        // never reach the writer and get baked into every profile.
        Assert.False(SkyrimResolutionService.TryParse(stored, out _));
    }

    [Fact]
    public void ApplyingAnUnparseableValueLeavesTheInstallAlone()
    {
        WriteInstall(activeProfile: "Ultra", profiles: ["Ultra"]);
        var before = File.ReadAllBytes(PrefsPath("Ultra"));

        Assert.False(SkyrimResolutionService.ApplyPreference(_root, "nonsense"));

        Assert.Equal(before, File.ReadAllBytes(PrefsPath("Ultra")));
    }

    [Fact]
    public void NoPreferenceLeavesTheInstallAlone()
    {
        WriteInstall(activeProfile: "Ultra", profiles: ["Ultra"]);
        var before = File.ReadAllBytes(PrefsPath("Ultra"));

        Assert.False(SkyrimResolutionService.ApplyPreference(_root, null));

        Assert.Equal(before, File.ReadAllBytes(PrefsPath("Ultra")));
    }

    [Fact]
    public void ApplyingWithNoProfilesReportsThatNothingWasWritten()
    {
        // Otherwise the UI says "set to 3440x1440 in every profile" having written nothing,
        // and the user goes looking for why the game ignored it.
        Directory.CreateDirectory(Path.Join(_root, "profiles"));
        WriteModOrganizerIni("Ultra");

        Assert.False(SkyrimResolutionService.ApplyPreference(_root, "3440x1440"));
    }

    [Theory]
    [InlineData("25640x1440")]
    [InlineData("1x1")]
    [InlineData("99999x99999")]
    public void RejectsResolutionsOutsideAnyPlausibleRange(string stored)
    {
        // A hand-edited settings.json must not bake an unlaunchable resolution into every
        // profile — "25640x1440" is precisely the typo the shipped LoreRim ini contains.
        Assert.False(SkyrimResolutionService.TryParse(stored, out _));
    }

    [Fact]
    public void ApplyingAValidPreferenceWritesIt()
    {
        WriteInstall(activeProfile: "Ultra", profiles: ["Ultra"]);

        Assert.True(SkyrimResolutionService.ApplyPreference(_root, "3440x1440"));

        Assert.Equal((3440, 1440), SkyrimResolutionService.Read(_root));
    }

    private string PrefsPath(string profile) =>
        Path.Join(_root, "profiles", profile, "skyrimprefs.ini");

    private void WriteInstall(string activeProfile, string[] profiles)
    {
        WriteModOrganizerIni(activeProfile);
        foreach (var profile in profiles)
        {
            Directory.CreateDirectory(Path.Join(_root, "profiles", profile));
            WriteBytes(
                PrefsPath(profile),
                "[Display]\r\n"
                    + "bBorderless =1\r\n"
                    + "iShadowMaskQuarter =4\r\n"
                    + "iSize H =2160\r\n"
                    + "iSize W =3840\r\n"
                    + "iVSyncPresentInterval =0\r\n"
            );
        }
    }

    private void WriteModOrganizerIni(string activeProfile) =>
        File.WriteAllText(
            Path.Join(_root, "ModOrganizer.ini"),
            $"[General]\r\nselected_profile=@ByteArray({activeProfile})\r\n"
        );

    /// <summary>Writes UTF-8 with a BOM, as BethINI does.</summary>
    private static void WriteBytes(string path, string content) =>
        File.WriteAllBytes(path, [.. new UTF8Encoding(true).GetPreamble(), .. Encoding.UTF8.GetBytes(content)]);
}
