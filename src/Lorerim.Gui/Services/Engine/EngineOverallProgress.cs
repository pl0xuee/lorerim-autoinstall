using System;

namespace Lorerim.Gui.Services.Engine;

/// <summary>
/// The overall install progress shown in the bottom status bar, derived from a single
/// file-progress line. <see cref="Indeterminate"/> means "working, but no numeric total" —
/// the bar animates rather than reading a fraction it cannot know.
/// </summary>
public readonly record struct EngineOverallProgress(bool Indeterminate, double Fraction)
{
    public static EngineOverallProgress From(EngineFileProgress p)
    {
        // The engine's (index/total) counter is optional and this engine version (0.5.7) often
        // omits it; without it there is no honest fraction, so animate instead of freezing at 0.
        if (p.Index is { } index && p.Total is { } total && total > 0)
        {
            // index is 1-based (file N of total): (N-1) files done, plus the current file's own
            // percent, so the bar also advances within a single large download.
            var fraction = (index - 1 + p.Percent / 100.0) / total;
            return new EngineOverallProgress(false, Math.Clamp(fraction, 0.0, 1.0));
        }
        return new EngineOverallProgress(true, 0.0);
    }
}
