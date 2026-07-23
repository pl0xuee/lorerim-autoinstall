using Lorerim.Gui.Services.Engine;
using Xunit;

namespace Lorerim.Tests;

/// <summary>
/// The bottom status-bar progress is derived from each file-progress line. When the engine
/// supplies an (index/total) counter it is a determinate, per-file-weighted fraction; without
/// one it is indeterminate so the bar animates instead of sitting frozen at zero.
/// </summary>
public class EngineOverallProgressTests
{
    private static EngineFileProgress Progress(double percent, int? index, int? total) =>
        new(EngineOperation.Downloading, "Some Mod.7z", percent, null, index, total);

    [Fact]
    public void CounterGivesADeterminateWeightedFraction()
    {
        // File 17 of 4402 at 50% → 16 whole files done plus half of the current one.
        var state = EngineOverallProgress.From(Progress(50, 17, 4402));

        Assert.False(state.Indeterminate);
        Assert.Equal((16 + 0.5) / 4402, state.Fraction, 6);
    }

    [Fact]
    public void NoCounterIsIndeterminate()
    {
        var state = EngineOverallProgress.From(Progress(42, null, null));

        Assert.True(state.Indeterminate);
    }

    [Fact]
    public void LastFileCompletedReachesFull()
    {
        var state = EngineOverallProgress.From(Progress(100, 4402, 4402));

        Assert.False(state.Indeterminate);
        Assert.Equal(1.0, state.Fraction, 6);
    }

    [Fact]
    public void ZeroTotalIsTreatedAsIndeterminateRatherThanDividingByZero()
    {
        var state = EngineOverallProgress.From(Progress(10, 0, 0));

        Assert.True(state.Indeterminate);
    }
}
