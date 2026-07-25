using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lorerim.Gui.Services;
using Lorerim.Gui.Services.Steam;

namespace Lorerim.Gui.ViewModels;

public partial class SteamSetupViewModel : ViewModelBase
{
    /// <summary>Same row type and glyphs as the install page's steps.</summary>
    public ObservableCollection<StepRow> Steps { get; } =
    [
        .. SteamIntegrationService.StepNames.Select(n => new StepRow(n)),
    ];

    /// <summary>Requirement results, shown as the same colour-coded checklist as the install page.</summary>
    public ObservableCollection<CheckRow> Checks { get; } = [];

    public ObservableCollection<CompatTool> CompatTools { get; } = [];

    [ObservableProperty]
    public partial CompatTool? SelectedTool { get; set; }

    // A rescan re-selects programmatically; only a deliberate ComboBox choice should outrank
    // the Proton pin from Settings on the next check.
    partial void OnSelectedToolChanged(CompatTool? value)
    {
        if (!_suppressToolDirty && value is not null)
        {
            _userPickedTool = value;
        }
    }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunSetup))]
    public partial bool PreflightOk { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunSetup))]
    public partial bool ConfirmRestart { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunSetup))]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// Every precondition the button depends on, so an unmet one greys it out instead of
    /// being explained only after the click. Includes the confirmation tick — a button that
    /// closes Steam must not look ready before the user has agreed to that.
    /// </summary>
    public bool CanRunSetup => PreflightOk && ConfirmRestart && !IsBusy;

    [ObservableProperty]
    public partial string AppName { get; set; } = InstallOrchestrator.SteamShortcutName;

    public SteamSetupViewModel(
        SettingsService settings,
        OperationRunner runner,
        SteamLocator steamLocator,
        CompatToolCatalog compatToolCatalog,
        SteamRuntimeCatalog steamRuntimes,
        SteamIntegrationService steamIntegration,
        ProtontricksService protontricks
    )
    {
        _settings = settings;
        _runner = runner;
        _steamLocator = steamLocator;
        _compatToolCatalog = compatToolCatalog;
        _steamRuntimes = steamRuntimes;
        _steamIntegration = steamIntegration;
        _protontricks = protontricks;

        // Any operation owns the whole app, so this page must not offer a second one — a
        // click during an install would fail with "another operation is running" and report
        // itself in the status bar as if the running install had failed.
        runner.Started += _ => Dispatcher.UIThread.Post(() => IsBusy = true);
        runner.Completed += (_, _) => Dispatcher.UIThread.Post(() => IsBusy = false);
    }

    /// <summary>
    /// Fills the checklist the first time the page is shown. Without it the page opens with an
    /// empty Proton dropdown and a greyed-out primary button explaining neither.
    /// </summary>
    public void EnsureCheckedOnce()
    {
        if (_checkedOnce)
        {
            return;
        }
        _checkedOnce = true;
        _ = PreflightAsync();
    }

    [RelayCommand]
    public async Task PreflightAsync()
    {
        try
        {
            await RunPreflightAsync();
        }
        catch (Exception e)
        {
            PreflightOk = false;
            Checks.Clear();
            Checks.Add(CheckRow.From(new PreflightCheck("Requirements", CheckState.Fail, e.Message)));
            StatusText = "Could not check requirements.";
        }
    }

    private async Task RunPreflightAsync()
    {
        var checks = new List<PreflightCheck>();

        _steam = _steamLocator.Locate();
        checks.Add(
            _steam is null
                ? new PreflightCheck(
                    "Steam",
                    CheckState.Fail,
                    "Native Steam not found (Flatpak/Snap Steam is not supported)."
                )
                : new PreflightCheck("Steam", CheckState.Ok, $"{_steam.Root} (user {_steam.UserId})")
        );

        var (ptOk, version) = await _protontricks.IsAvailableAsync();
        checks.Add(
            ptOk
                ? new PreflightCheck("protontricks", CheckState.Ok, version)
                : new PreflightCheck(
                    "protontricks",
                    CheckState.Fail,
                    "Not found — install it from your distro's repositories (e.g. pacman -S protontricks)."
                )
        );

        checks.Add(ScanProton());

        var installDir = Path.GetFullPath(Models.AppSettings.ExpandHome(_settings.Settings.InstallDir));
        _mo2Exe = Path.Join(installDir, "ModOrganizer.exe");
        if (!File.Exists(_mo2Exe))
        {
            // Off the UI thread: a populated LoreRim tree holds hundreds of thousands of
            // files, and this continuation otherwise runs on the UI thread.
            _mo2Exe = await Task.Run(() =>
                Directory.Exists(installDir)
                    ? Directory
                        .EnumerateFiles(installDir, "ModOrganizer.exe", SearchOption.AllDirectories)
                        .FirstOrDefault()
                    : null
            );
        }
        checks.Add(
            _mo2Exe is not null
                ? new PreflightCheck("Mod Organizer 2", CheckState.Ok, _mo2Exe)
                : new PreflightCheck(
                    "Mod Organizer 2",
                    CheckState.Fail,
                    $"ModOrganizer.exe not found under {installDir} — install LoreRim first."
                )
        );

        Checks.Clear();
        foreach (var check in checks)
        {
            Checks.Add(CheckRow.From(check));
        }
        PreflightOk = checks.All(c => c.State != CheckState.Fail);
        StatusText = PreflightOk
            ? "Ready. Tick the confirmation below, then run setup."
            : "Fix the blocking issues above before running setup.";
        foreach (var step in Steps)
        {
            step.Reset();
        }
    }

    /// <summary>
    /// Picks the Proton build the same way the one-click install does. Ranking alone is not
    /// enough: a build whose Steam Linux Runtime is absent cannot run at all, and choosing it
    /// here fails at the protontricks step — the exact failure the install path already
    /// avoids. The Settings pin is honoured unless it was overridden on this page.
    /// </summary>
    private PreflightCheck ScanProton()
    {
        var tools = _compatToolCatalog.Scan(_steam?.Root);
        var runtimeInstalled = _steamRuntimes.AvailabilityFor(_steam?.Root);
        var wanted = _userPickedTool?.InternalName ?? _settings.Settings.PreferredProtonInternalName;

        _suppressToolDirty = true;
        try
        {
            CompatTools.Clear();
            foreach (var tool in tools)
            {
                CompatTools.Add(tool);
            }
            SelectedTool = LorerimProton.Select(tools, runtimeInstalled, wanted).Tool;
        }
        finally
        {
            _suppressToolDirty = false;
        }
        return PreflightService.ProtonCheck(tools, runtimeInstalled, wanted);
    }

    [RelayCommand]
    private async Task RunSetupAsync()
    {
        // Claimed before the preflight below, not just before the run: preflight shells out to
        // protontricks and can walk the install tree, and until it returns the runner is not
        // yet busy — a second click in that window would reach RunAsync and come back as
        // "another operation is running", reading as a failure of the run already under way.
        if (IsBusy || _runner.IsBusy)
        {
            return;
        }
        IsBusy = true;
        try
        {
            await PreflightAsync();
            if (!PreflightOk || _steam is null || SelectedTool is null || _mo2Exe is null)
            {
                return;
            }
            if (!ConfirmRestart)
            {
                StatusText = "Tick the confirmation first — Steam will be closed and restarted.";
                return;
            }
            var ctx = new SteamSetupContext(
                _steam,
                SelectedTool,
                AppName,
                _mo2Exe,
                SteamIntegrationService.BuildLaunchOptions(
                    [_settings.Settings.InstallDir, _settings.Settings.DownloadDir]
                )
            );

            var result = await _runner.RunAsync(
                "Steam setup",
                (_, ct) =>
                    _steamIntegration.RunAsync(
                        ctx,
                        (i, state, detail) =>
                            Dispatcher.UIThread.Post(() => Steps[i].Apply(state, detail)),
                        ct
                    )
            );
            StatusText = result.Outcome switch
            {
                OperationOutcome.Succeeded =>
                    $"Done. '{AppName}' is in your Steam library with {ctx.Tool.DisplayName}. "
                        + "Launch it from Steam to start Mod Organizer 2.",
                OperationOutcome.Cancelled => "Steam setup was cancelled.",
                _ => $"Steam setup failed — {result.Error?.Message} (see the log for details).",
            };
        }
        finally
        {
            IsBusy = false;
        }
    }

    private SteamInstallation? _steam;
    private string? _mo2Exe;
    private CompatTool? _userPickedTool;
    private bool _suppressToolDirty;
    private bool _checkedOnce;
    private readonly SettingsService _settings;
    private readonly OperationRunner _runner;
    private readonly SteamLocator _steamLocator;
    private readonly CompatToolCatalog _compatToolCatalog;
    private readonly SteamRuntimeCatalog _steamRuntimes;
    private readonly SteamIntegrationService _steamIntegration;
    private readonly ProtontricksService _protontricks;
}
