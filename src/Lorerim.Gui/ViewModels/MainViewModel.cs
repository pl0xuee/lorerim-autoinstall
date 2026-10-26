using System;
using System.Collections.ObjectModel;
using System.Reactive.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lorerim.Gui.Services;
using Lorerim.Gui.Services.Engine;

namespace Lorerim.Gui.ViewModels;

/// <param name="Icon">SVG-style path data for a 16x16 icon. Drawn rather than typed as a glyph:
/// font fallback gave each symbol a different size, weight and even colour on the same rail.</param>
public sealed record NavItem(string Name, string Icon, ViewModelBase Page);

public static class NavIcons
{
    public const string Install =
        "F1 M7.25 1.5h1.5v6.1l2.2-2.2 1.06 1.06L8 10.22 3.99 6.46l1.06-1.06 2.2 2.2z M2.5 12h11v1.5h-11z";

    public const string Steam = "F1 M4 2.5v11l9-5.5z";

    // Three sliders: a bar with a round knob on it, repeated. Knobs wind the same way as the
    // bars so the non-zero fill unions them instead of cutting holes where they overlap.
    public const string Settings =
        "F1 M2 3.4h12v1.2H2z M10 2.1a1.9 1.9 0 1 1 0 3.8 1.9 1.9 0 1 1 0-3.8z "
        + "M2 7.4h12v1.2H2z M5.5 6.1a1.9 1.9 0 1 1 0 3.8 1.9 1.9 0 1 1 0-3.8z "
        + "M2 11.4h12v1.2H2z M11 10.1a1.9 1.9 0 1 1 0 3.8 1.9 1.9 0 1 1 0-3.8z";
}

public partial class MainViewModel : ViewModelBase
{
    public ObservableCollection<NavItem> NavItems { get; }
    public ObservableCollection<string> LogLines { get; } = [];

    [ObservableProperty]
    public partial NavItem? SelectedNavItem { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready";

    [ObservableProperty]
    public partial double OverallProgress { get; set; }

    // True while we know work is happening but have no numeric total to show a real fraction
    // (an engine version that omits the file counter, or a non-engine phase like Steam setup).
    // The bar animates instead of sitting frozen at zero.
    [ObservableProperty]
    public partial bool ProgressIsIndeterminate { get; set; }

    [ObservableProperty]
    public partial bool LogPaneOpen { get; set; }

    /// <summary>Where the full log is kept — the pane only holds the last 2000 lines.</summary>
    public string LogFilePath { get; }

    public MainViewModel(
        InstallViewModel install,
        SettingsViewModel settings,
        SteamSetupViewModel steamSetup,
        OperationRunner runner,
        LogService log,
        EngineProgress engineProgress,
        InstallOrchestrator orchestrator
    )
    {
        _runner = runner;
        _log = log;
        LogFilePath = log.LogFilePath ?? "(log file unavailable)";
        NavItems =
        [
            new NavItem("INSTALL", NavIcons.Install, install),
            new NavItem("STEAM", NavIcons.Steam, steamSetup),
            new NavItem("SETTINGS", NavIcons.Settings, settings),
        ];
        SelectedNavItem = NavItems[0];

        log.LineAdded += line =>
            Dispatcher.UIThread.Post(() =>
            {
                LogLines.Add(line);
                while (LogLines.Count > 2000)
                {
                    LogLines.RemoveAt(0);
                }
                // Mirror activity into the status bar during an operation so steps that
                // report no engine progress (protontricks, prefix creation) aren't silent
                // with the log pane closed. First line only: multi-line entries are error
                // dumps that belong in the pane, not squeezed into one status row.
                if (IsBusy)
                {
                    StatusText = FirstLine(StripTimestamp(line));
                }
            });

        static string StripTimestamp(string line) =>
            line.Length > 10 && line[0] == '[' && line[9] == ']' ? line[10..] : line;

        runner.Started += name =>
            Dispatcher.UIThread.Post(() =>
            {
                IsBusy = true;
                OverallProgress = 0;
                // Start animating right away: many phases (Steam setup, protontricks) never emit
                // a file counter, so an indeterminate bar is the honest default until one arrives.
                ProgressIsIndeterminate = true;
                StatusText = $"{name}…";
            });
        runner.Completed += (name, result) =>
            Dispatcher.UIThread.Post(() =>
            {
                IsBusy = false;
                StatusText = result.Outcome switch
                {
                    OperationOutcome.Succeeded => $"{name}: done",
                    OperationOutcome.Cancelled => $"{name}: cancelled",
                    // First line only: failures such as a failed preflight or missing CC content
                    // carry a bulleted list, and the one-row status bar would grow to hold it.
                    // The whole message is in the log pane, which opens below.
                    _ => $"{name}: failed — {FirstLine(result.Error?.Message)}",
                };
                if (result.Outcome == OperationOutcome.Failed)
                {
                    LogPaneOpen = true;
                }
            });

        // Only the engine phase can report a fraction. Every other phase — Steam setup,
        // prefix creation, the 45-minute protontricks step — reports nothing per file, so
        // hand the bar back to its animation on leaving the engine instead of leaving it
        // parked on the last fraction the engine happened to publish.
        orchestrator.PhaseChanged += change =>
            Dispatcher.UIThread.Post(() =>
            {
                if (change.Phase != InstallPhase.EngineInstall)
                {
                    ProgressIsIndeterminate = true;
                }
            });

        // The engine emits progress lines at high frequency; sample before touching the UI thread.
        Observable
            .FromEvent<EngineFileProgress>(
                h => engineProgress.FileProgressChanged += h,
                h => engineProgress.FileProgressChanged -= h
            )
            .Sample(TimeSpan.FromMilliseconds(150))
            .Subscribe(e =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsBusy)
                    {
                        return;
                    }
                    var counter = e.Index is { } i && e.Total is { } t ? $" [{i}/{t}]" : "";
                    StatusText = $"{e.Operation}: {e.FileName} {e.Percent:F0}%{counter}";
                    var progress = EngineOverallProgress.From(e);
                    ProgressIsIndeterminate = progress.Indeterminate;
                    if (!progress.Indeterminate)
                    {
                        OverallProgress = progress.Fraction;
                    }
                })
            );
    }

    /// <summary>
    /// The one-row status bar shows a single line. A message that continues says so, rather
    /// than ending on a bare "Preflight failed:" that reads as though nothing more is known.
    /// </summary>
    internal static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }
        text = text.Trim();
        var newline = text.IndexOfAny(['\r', '\n']);
        return newline < 0 ? text : text[..newline].TrimEnd(':', ' ') + " — see the log for details";
    }

    [RelayCommand]
    private void Cancel() => _runner.Cancel();

    [RelayCommand]
    private void ToggleLogPane() => LogPaneOpen = !LogPaneOpen;

    /// <summary>
    /// Opens the log file in the desktop's default handler. The pane is capped at 2000 lines
    /// and a LoreRim install runs for hours, so the record a user needs to report a failure
    /// is routinely off the top of it.
    /// </summary>
    [RelayCommand]
    private void OpenLogFile()
    {
        if (!Services.SafeUrl.TryOpenLocalPath(_log.LogFilePath))
        {
            _log.Append($"Could not open the log file at {LogFilePath}");
        }
    }

    private readonly OperationRunner _runner;
    private readonly LogService _log;
}
