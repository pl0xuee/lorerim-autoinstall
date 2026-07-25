using Lorerim.Gui.Services.Steam;
using Lorerim.Gui.ViewModels;
using Xunit;

namespace Lorerim.Tests;

/// <summary>
/// The step rows on the install and Steam pages colour their glyph from these flags. A failed
/// step that still reports itself as running (or as neither) renders in the wrong tone, which
/// is the difference between "this is working" and "this is broken" on a page a user watches
/// for hours.
/// </summary>
public class StepRowTests
{
    [Fact]
    public void APendingRowIsNeitherRunningNorFinished()
    {
        var row = new StepRow("Create Proton prefix");

        Assert.Equal(StepRow.PendingGlyph, row.Glyph);
        Assert.False(row.IsRunning);
        Assert.False(row.IsOk);
        Assert.False(row.IsFail);
    }

    [Theory]
    [InlineData(StepState.Running, true, false, false)]
    [InlineData(StepState.Ok, false, true, false)]
    [InlineData(StepState.Failed, false, false, true)]
    public void EachStateLightsExactlyOneTone(
        StepState state,
        bool running,
        bool ok,
        bool fail
    )
    {
        var row = new StepRow("Restart Steam");

        row.Apply(state, "detail");

        Assert.Equal(running, row.IsRunning);
        Assert.Equal(ok, row.IsOk);
        Assert.Equal(fail, row.IsFail);
        Assert.Equal("detail", row.Detail);
    }

    [Fact]
    public void ChangingTheGlyphRaisesTheToneFlags()
    {
        // The flags are computed from Glyph, so without change notification the row would
        // keep its old colour after the state moved on.
        var row = new StepRow("Shut down Steam");
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.Apply(StepState.Ok, "");

        Assert.Contains(nameof(StepRow.IsRunning), raised);
        Assert.Contains(nameof(StepRow.IsOk), raised);
        Assert.Contains(nameof(StepRow.IsFail), raised);
    }

    [Fact]
    public void ResetReturnsARunningRowToPending()
    {
        // A cancelled run leaves rows mid-flight; they must not spin forever.
        var row = new StepRow("Install prerequisites (protontricks)");
        row.Apply(StepState.Running, "working");

        row.Reset();

        Assert.Equal(StepRow.PendingGlyph, row.Glyph);
        Assert.False(row.IsRunning);
        Assert.Equal("", row.Detail);
    }
}
