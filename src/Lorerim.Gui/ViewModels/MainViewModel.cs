using System;
using System.Collections.ObjectModel;
using System.Reactive.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lorerim.Gui.Services;
using Lorerim.Gui.Services.Engine;

namespace Lorerim.Gui.ViewModels;

public sealed record NavItem(string Name, string Icon, ViewModelBase Page);

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

    public MainViewModel(
        InstallViewModel install,
        SettingsViewModel settings,
        SteamSetupViewModel steamSetup,
        OperationRunner runner,
        LogService log,
        EngineProgress engineProgress
    )
    {
        _runner = runner;
        NavItems =
        [
            new NavItem("INSTALL", "⇣", install),
            new NavItem("STEAM", "▶", steamSetup),
            new NavItem("SETTINGS", "⚙︎", settings),
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
                    var text = StripTimestamp(line);
                    var newline = text.IndexOf('\n');
                    StatusText = (newline >= 0 ? text[..newline] : text).Trim();
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
                    _ => $"{name}: failed — {result.Error?.Message}",
                };
                if (result.Outcome == OperationOutcome.Failed)
                {
                    LogPaneOpen = true;
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

    [RelayCommand]
    private void Cancel() => _runner.Cancel();

    [RelayCommand]
    private void ToggleLogPane() => LogPaneOpen = !LogPaneOpen;

    private readonly OperationRunner _runner;
}
