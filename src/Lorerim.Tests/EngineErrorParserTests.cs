using System.Collections.Generic;
using Lorerim.Gui.Services.Engine;
using Xunit;

namespace Lorerim.Tests;

/// <summary>
/// jackify-engine reports failures as one JSON object per stderr line and exits with a code
/// that names the class of failure. The app used to discard both and say "exited with code 6",
/// hours into an install, with the actual cause only in a raw JSON line in the log.
/// </summary>
public class EngineErrorParserTests
{
    [Fact]
    public void ReadsADiskFullError()
    {
        var error = EngineErrorParser.TryParse(
            """{"je":"1","level":"error","type":"disk_full","message":"Disk is full. Free up space and try again."}"""
        );
        Assert.NotNull(error);
        Assert.Equal("disk_full", error.Type);
        Assert.StartsWith("Disk full: Disk is full.", error.Explanation);
        Assert.Contains("resumes where it stopped", error.Explanation);
        Assert.DoesNotContain('\n', error.Explanation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[FILE_PROGRESS] Downloading: a.7z (4%)")]
    [InlineData("Installing 4402 archives")]
    [InlineData("{not json")]
    [InlineData("""{"event":"manual_download_required","name":"x"}""")]
    [InlineData("""{"type":"disk_full","message":"no marker"}""")]
    [InlineData("""{"je":"2","type":"disk_full","message":"wrong marker"}""")]
    [InlineData("[1,2,3]")]
    public void IgnoresAnythingThatIsNotAStructuredError(string line) =>
        Assert.Null(EngineErrorParser.TryParse(line));

    [Fact]
    public void WarningsAreNotErrors() =>
        Assert.Null(
            EngineErrorParser.TryParse(
                """{"je":"1","level":"warning","type":"network_error","message":"retrying"}"""
            )
        );

    [Fact]
    public void AMarkerWrittenAsANumberIsStillRecognised() =>
        Assert.NotNull(
            EngineErrorParser.TryParse("""{"je":1,"type":"network_error","message":"offline"}""")
        );

    [Fact]
    public void ALineWithNoTypeIsAGenericEngineError()
    {
        var error = EngineErrorParser.TryParse("""{"je":"1","message":"something broke"}""");
        Assert.NotNull(error);
        Assert.Equal("engine_error", error.Type);
        Assert.Equal("Install engine error: something broke", error.Explanation);
    }

    [Fact]
    public void AnUnknownTypeKeepsItsNameInsteadOfBeingDropped()
    {
        var error = EngineErrorParser.TryParse("""{"je":"1","type":"brand_new","message":"hi"}""");
        Assert.NotNull(error);
        Assert.Equal("brand_new: hi", error.Explanation);
    }

    [Fact]
    public void ASingleMismatchedGameFileNamesTheFileAndTheExpectedVersion()
    {
        var error = EngineErrorParser.TryParse(
            """
            {"je":"1","type":"missing_archives","message":"Unable to download 1 archive.",
             "context":{"game_file_mismatches":[{"name":"SkyrimSE.exe","game_version":"1.6.1170"}],
                        "game_file_total":1,"download_failures":[]}}
            """.Replace("\n", " ")
        );
        Assert.NotNull(error);
        Assert.Contains("'SkyrimSE.exe'", error.Explanation);
        Assert.Contains("1.6.1170", error.Explanation);
        Assert.Contains("verify them in Steam", error.Explanation);
        Assert.DoesNotContain("failed to download", error.Explanation);
    }

    [Fact]
    public void ManyMismatchedGameFilesAreCountedAgainstTheTotal()
    {
        var error = EngineErrorParser.TryParse(
            """
            {"je":"1","type":"missing_archives","message":"x",
             "context":{"game_file_mismatches":[{"name":"a","game_version":"1.6.1170"},{"name":"b","game_version":"1.6.1170"}],
                        "game_file_total":40}}
            """.Replace("\n", " ")
        );
        Assert.NotNull(error);
        Assert.Contains("2 of 40 game files", error.Explanation);
    }

    [Fact]
    public void MixedVersionsAreNotPresentedAsOneExpectedVersion()
    {
        var error = EngineErrorParser.TryParse(
            """
            {"je":"1","type":"missing_archives","message":"x",
             "context":{"game_file_mismatches":[{"name":"a","game_version":"1.6.1170"},{"name":"b","game_version":"1.5.97"}]}}
            """.Replace("\n", " ")
        );
        Assert.NotNull(error);
        Assert.DoesNotContain("expects game version", error.Explanation);
    }

    [Fact]
    public void DownloadFailuresListFiveNamesThenCountTheRest()
    {
        var error = EngineErrorParser.TryParse(
            """
            {"je":"1","type":"missing_archives","message":"x",
             "context":{"download_failures":[{"name":"a.7z"},{"name":"b.7z"},{"name":"c.7z"},
                         {"name":"d.7z"},{"name":"e.7z"},{"name":"f.7z"},{"name":"g.7z"}]}}
            """.Replace("\n", " ")
        );
        Assert.NotNull(error);
        Assert.Contains("7 file(s) failed to download: a.7z, b.7z, c.7z, d.7z, e.7z, and 2 more", error.Explanation);
        Assert.DoesNotContain("g.7z", error.Explanation);
    }

    [Fact]
    public void BothKindsOfFailureAreReportedTogether()
    {
        var error = EngineErrorParser.TryParse(
            """
            {"je":"1","type":"missing_archives","message":"x",
             "context":{"game_file_mismatches":[{"name":"SkyrimSE.exe"}],"download_failures":[{"name":"a.7z"}]}}
            """.Replace("\n", " ")
        );
        Assert.NotNull(error);
        Assert.Contains("'SkyrimSE.exe'", error.Explanation);
        Assert.Contains("1 file(s) failed to download: a.7z", error.Explanation);
    }

    [Theory]
    [InlineData("""{"je":"1","type":"missing_archives","message":"Unable to download 3 archives."}""")]
    [InlineData("""{"je":"1","type":"missing_archives","message":"Unable to download 3 archives.","context":null}""")]
    [InlineData("""{"je":"1","type":"missing_archives","message":"Unable to download 3 archives.","context":[]}""")]
    public void MissingArchivesWithoutUsableContextFallBackToTheEnginesOwnWords(string line)
    {
        var error = EngineErrorParser.TryParse(line);
        Assert.NotNull(error);
        Assert.Equal("Unable to download 3 archives.", error.Explanation);
    }

    [Theory]
    [InlineData(2, "Nexus sign-in failed")]
    [InlineData(3, "Network error")]
    [InlineData(4, "Disk full")]
    [InlineData(5, "Validation failed")]
    public void ExitCodesNameTheirFailureClass(int code, string expectedStart) =>
        Assert.StartsWith(expectedStart, EngineErrorParser.ForExitCode(code));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(139)]
    public void OtherExitCodesNameNothing(int code) =>
        Assert.Null(EngineErrorParser.ForExitCode(code));
}

public class EngineFailureMessageTests
{
    private static readonly List<string> NoCc = [];

    private static EngineStructuredError Structured(string type) =>
        EngineErrorParser.TryParse($$"""{"je":"1","type":"{{type}}","message":"boom"}""")!;

    [Fact]
    public void MissingCcContentKeepsItsSpecificGuidanceOverAStructuredError()
    {
        var message = JackifyEngineRunner.BuildFailureMessage(
            6,
            ["ccBGSSSE001-Fish.esm is missing"],
            false,
            [Structured("missing_archives")]
        );
        Assert.Contains("Creations menu", message);
    }

    [Fact]
    public void ACreationKitFailureKeepsItsGuidanceToo()
    {
        var message = JackifyEngineRunner.BuildFailureMessage(6, NoCc, true, [Structured("engine_error")]);
        Assert.Contains("Creation Kit", message);
    }

    [Fact]
    public void AStructuredErrorBeatsTheExitCode()
    {
        var message = JackifyEngineRunner.BuildFailureMessage(4, NoCc, false, [Structured("auth_failed")]);
        Assert.StartsWith("Nexus sign-in failed", message);
    }

    [Fact]
    public void TheLastStructuredErrorIsTheOneThatEndedTheRun()
    {
        var message = JackifyEngineRunner.BuildFailureMessage(
            3,
            NoCc,
            false,
            [Structured("download_stalled"), Structured("network_error")]
        );
        Assert.StartsWith("Network error", message);
    }

    [Fact]
    public void WithoutStructuredErrorsTheExitCodeStillSaysSomething() =>
        Assert.StartsWith("Disk full", JackifyEngineRunner.BuildFailureMessage(4, NoCc, false));

    [Fact]
    public void AnUnnamedExitCodeKeepsTheGenericMessage() =>
        Assert.Equal(
            "jackify-engine exited with code 6 — see the log for details.",
            JackifyEngineRunner.BuildFailureMessage(6, NoCc, false)
        );
}
