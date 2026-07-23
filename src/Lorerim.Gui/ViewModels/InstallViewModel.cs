using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lorerim.Gui.Services;
using Lorerim.Gui.Services.Engine;
using Lorerim.Gui.Services.Nexus;

namespace Lorerim.Gui.ViewModels;

public partial class FileProgressRow : ObservableObject
{
    public required string Name { get; init; }

    [ObservableProperty]
    public partial string Phase { get; set; } = "";

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string Speed { get; set; } = "";
}

public partial class PhaseRow(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial string State { get; set; } = "";

    [ObservableProperty]
    public partial string Detail { get; set; } = "";
}

public partial class InstallViewModel : ViewModelBase
{
    public ObservableCollection<FileProgressRow> FileRows { get; } = [];
    public ObservableCollection<PreflightCheck> Checks { get; } = [];
    public ObservableCollection<PhaseRow> Phases { get; } = [];
    public ObservableCollection<ManualDownload> ManualDownloads { get; } = [];

    [ObservableProperty]
    public partial string InstallDir { get; set; }

    [ObservableProperty]
    public partial string DownloadDir { get; set; }

    [ObservableProperty]
    public partial string AuthStatus { get; set; } = "Not signed in";

    [ObservableProperty]
    public partial bool IsSignedIn { get; set; }

    [ObservableProperty]
    public partial string OverallCounter { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowFirstLaunchGuide { get; set; }

    [ObservableProperty]
    public partial bool ShowManualDownloads { get; set; }

    [ObservableProperty]
    public partial string CheckResultText { get; set; } = "";

    public InstallViewModel(
        OperationRunner runner,
        SettingsService settings,
        LogService log,
        EngineProgress engineProgress,
        InstallOrchestrator orchestrator,
        PreflightService preflightService,
        NexusOAuthService oauth,
        NexusTokenStore tokenStore,
        JackifyEngineRunner engineRunner
    )
    {
        _runner = runner;
        _settings = settings;
        _log = log;
        _orchestrator = orchestrator;
        _preflightService = preflightService;
        _oauth = oauth;
        _tokenStore = tokenStore;
        _engineRunner = engineRunner;

        InstallDir = settings.Settings.InstallDir;
        DownloadDir = settings.Settings.DownloadDir;

        foreach (var phase in PhaseNames.Keys)
        {
            Phases.Add(new PhaseRow(PhaseNames[phase]));
        }

        // Batch (not sample) so no per-file terminal state is lost, then update rows on the UI thread.
        Observable
            .FromEvent<EngineFileProgress>(
                h => engineProgress.FileProgressChanged += h,
                h => engineProgress.FileProgressChanged -= h
            )
            .Buffer(TimeSpan.FromMilliseconds(250))
            .Where(batch => batch.Count > 0)
            .Subscribe(batch => Dispatcher.UIThread.Post(() => ApplyProgressBatch(batch)));

        engineProgress.ManualDownloadsRequested += downloads =>
            Dispatcher.UIThread.Post(() =>
            {
                ManualDownloads.Clear();
                foreach (var d in downloads)
                {
                    ManualDownloads.Add(d);
                }
                ShowManualDownloads = true;
            });

        orchestrator.PhaseChanged += change =>
            Dispatcher.UIThread.Post(() =>
            {
                var row = Phases[(int)change.Phase];
                row.State = change.State switch
                {
                    Services.Steam.StepState.Running => "…",
                    Services.Steam.StepState.Ok => "✅",
                    _ => "❌",
                };
                row.Detail = change.Detail;
                if (change.Phase == InstallPhase.Done && change.State == Services.Steam.StepState.Ok)
                {
                    ShowFirstLaunchGuide = true;
                }
            });

        orchestrator.SteamStepChanged += (index, state, detail) =>
        {
            var name = Services.Steam.SteamIntegrationService.StepNames[index];
            _log.Append(
                state switch
                {
                    Services.Steam.StepState.Running => $"Steam setup: {name}…",
                    Services.Steam.StepState.Ok => $"Steam setup: {name} ✅",
                    _ => $"Steam setup: {name} FAILED — {detail}",
                }
            );
        };

        RefreshAuthStatus();
    }

    private static readonly Dictionary<InstallPhase, string> PhaseNames = new()
    {
        [InstallPhase.Preflight] = "Preflight checks",
        [InstallPhase.Auth] = "Nexus sign-in",
        [InstallPhase.ResolveModlist] = "Resolve modlist",
        [InstallPhase.EngineInstall] = "Download & install (this takes hours)",
        [InstallPhase.VerifyInstall] = "Verify install",
        [InstallPhase.SteamSetup] = "Steam setup",
        [InstallPhase.Done] = "Done",
    };

    /// <summary>The one-click entry point.</summary>
    [RelayCommand]
    private async Task InstallAsync()
    {
        if (_runner.IsBusy)
        {
            return;
        }
        await PersistDirsAsync();
        Dispatcher.UIThread.Post(() =>
        {
            FileRows.Clear();
            ShowFirstLaunchGuide = false;
            foreach (var p in Phases)
            {
                p.State = "";
                p.Detail = "";
            }
        });
        _rowLookup.Clear();

        await _runner.RunAsync("LoreRim install", (_, ct) => _orchestrator.RunAsync(ct));
    }

    [RelayCommand]
    private async Task RunPreflightAsync()
    {
        if (_runner.IsBusy)
        {
            return;
        }
        await PersistDirsAsync();
        CheckResultText = "Checking…";
        try
        {
            var checks = await _preflightService.RunAsync(
                InstallDir,
                DownloadDir,
                CancellationToken.None
            );
            Dispatcher.UIThread.Post(() =>
            {
                Checks.Clear();
                foreach (var c in checks)
                {
                    Checks.Add(c);
                }
                CheckResultText = checks.Any(c => c.State == CheckState.Fail)
                    ? "Some checks failed — fix them before installing."
                    : "All checks passed.";
            });
        }
        catch (Exception e)
        {
            CheckResultText = $"Preflight error: {e.Message}";
        }
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (_signInBusy)
        {
            return;
        }
        _signInBusy = true;
        try
        {
            AuthStatus = "Waiting for browser…";
            var token = await _oauth.AuthorizeAsync(CancellationToken.None);
            if (token is null)
            {
                AuthStatus = "Sign-in failed — see log";
            }
            RefreshAuthStatus();
        }
        finally
        {
            _signInBusy = false;
        }
    }

    [RelayCommand]
    private void ContinueManualDownloads()
    {
        ShowManualDownloads = false;
        _engineRunner.ContinueManualDownloads();
    }

    [RelayCommand]
    private void Cancel() => _runner.Cancel();

    public void RefreshAuthStatus()
    {
        var token = _tokenStore.Load();
        if (token?.AccessToken is not null)
        {
            IsSignedIn = true;
            AuthStatus = token.UserName is not null
                ? $"Signed in as {token.UserName}{(token.IsPremium ? " (Premium)" : "")}"
                : "Signed in";
        }
        else if (!string.IsNullOrEmpty(_settings.Settings.NexusApiKey))
        {
            IsSignedIn = true;
            AuthStatus = "Using API key";
        }
        else
        {
            IsSignedIn = false;
            AuthStatus = "Not signed in";
        }
    }

    private async Task PersistDirsAsync()
    {
        _settings.Settings.InstallDir = InstallDir;
        _settings.Settings.DownloadDir = DownloadDir;
        await _settings.SaveAsync();
    }

    private void ApplyProgressBatch(IList<EngineFileProgress> batch)
    {
        foreach (var e in batch)
        {
            if (e.Index is { } i && e.Total is { } t)
            {
                OverallCounter = $"{i}/{t}";
            }
            if (string.IsNullOrEmpty(e.FileName) || e.FileName == "__phase_progress__")
            {
                continue;
            }
            if (!_rowLookup.TryGetValue(e.FileName, out var row))
            {
                row = new FileProgressRow { Name = e.FileName };
                _rowLookup[e.FileName] = row;
                FileRows.Add(row);
                // Keep the grid bounded; finished rows age out from the top.
                while (FileRows.Count > 500)
                {
                    _rowLookup.Remove(FileRows[0].Name);
                    FileRows.RemoveAt(0);
                }
            }
            row.Phase = e.Operation.ToString();
            row.Progress = e.Percent / 100.0;
            row.Speed = e.Speed ?? "";
        }
    }

    private readonly OperationRunner _runner;
    private readonly SettingsService _settings;
    private readonly LogService _log;
    private readonly InstallOrchestrator _orchestrator;
    private readonly PreflightService _preflightService;
    private readonly NexusOAuthService _oauth;
    private readonly NexusTokenStore _tokenStore;
    private readonly JackifyEngineRunner _engineRunner;
    private readonly Dictionary<string, FileProgressRow> _rowLookup = [];
    private bool _signInBusy;
}
