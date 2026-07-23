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

public partial class SetupStep(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial string Status { get; set; } = "•";

    [ObservableProperty]
    public partial string Detail { get; set; } = "";

    public void Reset() => (Status, Detail) = ("•", "");

    public void Apply(StepState state, string detail) =>
        (Status, Detail) = (
            state switch
            {
                StepState.Running => "⏳",
                StepState.Ok => "✅",
                _ => "❌",
            },
            detail
        );
}

public partial class SteamSetupViewModel : ViewModelBase
{
    public ObservableCollection<SetupStep> Steps { get; } =
    [
        .. SteamIntegrationService.StepNames.Select(n => new SetupStep(n)),
    ];

    public ObservableCollection<CompatTool> CompatTools { get; } = [];

    [ObservableProperty]
    public partial CompatTool? SelectedTool { get; set; }

    [ObservableProperty]
    public partial string PreflightText { get; set; } = "";

    [ObservableProperty]
    public partial bool PreflightOk { get; set; }

    [ObservableProperty]
    public partial bool ConfirmRestart { get; set; }

    [ObservableProperty]
    public partial string AppName { get; set; } = InstallOrchestrator.SteamShortcutName;

    public SteamSetupViewModel(
        SettingsService settings,
        OperationRunner runner,
        SteamLocator steamLocator,
        CompatToolCatalog compatToolCatalog,
        SteamIntegrationService steamIntegration,
        ProtontricksService protontricks
    )
    {
        _settings = settings;
        _runner = runner;
        _steamLocator = steamLocator;
        _compatToolCatalog = compatToolCatalog;
        _steamIntegration = steamIntegration;
        _protontricks = protontricks;
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
            PreflightText = $"Preflight failed: {e.Message}";
        }
    }

    private async Task RunPreflightAsync()
    {
        var problems = new List<string>();
        var infos = new List<string>();

        _steam = _steamLocator.Locate();
        if (_steam is null)
        {
            problems.Add("Native Steam installation not found (Flatpak Steam is not supported).");
        }
        else
        {
            infos.Add($"Steam: {_steam.Root} (user {_steam.UserId})");
        }

        var (ptOk, version) = await _protontricks.IsAvailableAsync();
        if (ptOk)
        {
            infos.Add($"protontricks: {version}");
        }
        else
        {
            problems.Add("protontricks not found — install it (e.g. `sudo pacman -S protontricks`).");
        }

        var previousTool = SelectedTool;
        CompatTools.Clear();
        foreach (var tool in _compatToolCatalog.Scan(_steam?.Root))
        {
            CompatTools.Add(tool);
        }
        // A rescan must not silently override the user's explicit Proton choice.
        SelectedTool =
            CompatTools.FirstOrDefault(t => t.InternalName == previousTool?.InternalName)
            ?? CompatTools.FirstOrDefault();
        if (SelectedTool is null)
        {
            problems.Add(
                "No Proton found in compatibilitytools.d (install proton-cachyos or GE-Proton)."
            );
        }
        else
        {
            infos.Add($"Proton: {SelectedTool.DisplayName}");
        }

        var installDir = Path.GetFullPath(_settings.Settings.InstallDir);
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
        if (_mo2Exe is not null)
        {
            infos.Add($"MO2: {_mo2Exe}");
        }
        else
        {
            problems.Add(
                $"ModOrganizer.exe not found under {installDir} — install LoreRim first."
            );
        }

        PreflightOk = problems.Count == 0;
        PreflightText = string.Join("\n", problems.Concat(infos));
        foreach (var step in Steps)
        {
            step.Reset();
        }
    }

    [RelayCommand]
    private async Task RunSetupAsync()
    {
        await PreflightAsync();
        if (!PreflightOk || _steam is null || SelectedTool is null || _mo2Exe is null)
        {
            return;
        }
        if (!ConfirmRestart)
        {
            PreflightText = "Tick the confirmation checkbox first — Steam will be closed and restarted.";
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
                    (i, state, detail) => Dispatcher.UIThread.Post(() => Steps[i].Apply(state, detail)),
                    ct
                )
        );
        if (result.Outcome == OperationOutcome.Succeeded)
        {
            PreflightText =
                $"Done. '{AppName}' is in your Steam library with {ctx.Tool.DisplayName}. Launch it from Steam to start Mod Organizer 2.";
        }
    }

    private SteamInstallation? _steam;
    private string? _mo2Exe;
    private readonly SettingsService _settings;
    private readonly OperationRunner _runner;
    private readonly SteamLocator _steamLocator;
    private readonly CompatToolCatalog _compatToolCatalog;
    private readonly SteamIntegrationService _steamIntegration;
    private readonly ProtontricksService _protontricks;
}
