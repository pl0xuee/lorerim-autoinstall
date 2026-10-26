using Lorerim.Gui.ViewModels;
using Xunit;

namespace Lorerim.Tests;

/// <summary>
/// The status bar is one row. A failed install carries a bulleted list (every blocking
/// preflight issue, the missing CC files); letting it through made the bar grow to three lines.
/// </summary>
public class StatusLineTests
{
    [Fact]
    public void AMultiLineMessageIsCutToItsFirstLineAndPointsAtTheLog()
    {
        var shown = MainViewModel.FirstLine("Preflight failed (2 blocking issues):\n• Steam: gone\n• Disk: full");
        Assert.Equal("Preflight failed (2 blocking issues) — see the log for details", shown);
    }

    [Fact]
    public void ASingleLineMessageIsUntouched() =>
        Assert.Equal("Downloading: a.7z 41%", MainViewModel.FirstLine("Downloading: a.7z 41%"));

    [Fact]
    public void WindowsLineEndingsAreTreatedLikeUnixOnes() =>
        Assert.StartsWith("First", MainViewModel.FirstLine("First\r\nSecond"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingToShowGivesAnEmptyLine(string? message) =>
        Assert.Equal("", MainViewModel.FirstLine(message));

    [Fact]
    public void TheSkyrimContentFailureStaysReadableInOneLine()
    {
        var message =
            "Skyrim Anniversary Edition content is missing. Launch Skyrim once from Steam.\nccBGSSSE001-Fish.esm missing";
        Assert.StartsWith("Skyrim Anniversary Edition content is missing.", MainViewModel.FirstLine(message));
    }
}

public class ProtontricksComponentsTests
{
    /// <summary>Jackify 0.8.1.3 added it to every Skyrim modlist's prefix.</summary>
    [Fact]
    public void IncludesTheDirectX9ComponentJackifyAddedForSkyrim() =>
        Assert.Contains("d3dx9_42", Lorerim.Gui.Services.Steam.ProtontricksService.Components);
}
