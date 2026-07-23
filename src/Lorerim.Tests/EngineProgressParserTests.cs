using Lorerim.Gui.Services.Engine;
using Xunit;

namespace Lorerim.Tests;

public class EngineProgressParserTests
{
    [Fact]
    public void ParsesFullLine()
    {
        var p = EngineProgressParser.TryParse(
            "[FILE_PROGRESS] Downloading: Some Mod-1.7z (42.3%) [12.3MB/s] (17/4402)"
        );
        Assert.NotNull(p);
        Assert.Equal(EngineOperation.Downloading, p.Operation);
        Assert.Equal("Some Mod-1.7z", p.FileName);
        Assert.Equal(42.3, p.Percent, 3);
        Assert.Equal("12.3MB/s", p.Speed);
        Assert.Equal(17, p.Index);
        Assert.Equal(4402, p.Total);
    }

    [Fact]
    public void ParsesLineWithoutSpeedAndCounter()
    {
        var p = EngineProgressParser.TryParse("[FILE_PROGRESS] Extracting: textures.bsa (99%)");
        Assert.NotNull(p);
        Assert.Equal(EngineOperation.Extracting, p.Operation);
        Assert.Null(p.Speed);
        Assert.Null(p.Index);
    }

    [Fact]
    public void CompletedForcesHundredPercent()
    {
        var p = EngineProgressParser.TryParse("[FILE_PROGRESS] Completed: foo.esp (12%)");
        Assert.NotNull(p);
        Assert.Equal(100.0, p.Percent);
    }

    [Theory]
    [InlineData("plain log line")]
    [InlineData("")]
    [InlineData("[OTHER] Downloading: x (5%)")]
    public void IgnoresNonProgressLines(string line)
    {
        Assert.Null(EngineProgressParser.TryParse(line));
    }

    [Fact]
    public void DetectsCcContentErrors()
    {
        Assert.True(
            JackifyEngineRunner.IsCcContentError(
                "Error: missing game file Data_ccbgssse025-advdsgs.bsa"
            )
        );
        Assert.False(JackifyEngineRunner.IsCcContentError("Downloading ccbgssse025-advdsgs.bsa"));
        Assert.False(JackifyEngineRunner.IsCcContentError("Error: missing archive foo.7z"));
    }

    [Fact]
    public void DetectsCreationKitErrors()
    {
        Assert.True(
            JackifyEngineRunner.IsCreationKitMissingError(
                "GameFileSource missing: CreationKit.exe"
            )
        );
        Assert.False(JackifyEngineRunner.IsCreationKitMissingError("missing CreationKit.exe"));
    }
}
