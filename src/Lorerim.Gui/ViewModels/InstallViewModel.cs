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
using Lorerim.Gui.Services.Display;
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

    /// <summary>Glyph column: pending until the orchestrator reports on this phase.</summary>
    [ObservableProperty]
    public partial string Glyph { get; set; } = PendingGlyph;

    [ObservableProperty]
    public partial string Detail { get; set; } = "";

    /// <summary>Drives emphasis: pending phases sit back, the live one comes forward.</summary>
    [ObservableProperty]
    public partial double Emphasis { get; set; } = 0.45;

    public bool IsRunning => Glyph == "◐";

    public void Reset()
    {
        Glyph = PendingGlyph;
        Detail = "";
        Emphasis = 0.45;
    }

    public void Apply(Services.Steam.StepState state, string detail)
    {
        (Glyph, Emphasis) = state switch
        {
            Services.Steam.StepState.Running => ("◐", 1.0),
            Services.Steam.StepState.Ok => ("✓", 0.8),
            _ => ("✕", 1.0),
        };
        Detail = detail;
    }

    private const string PendingGlyph = "○";
}

/// <summary>A preflight result dressed for display (glyph + colour key).</summary>
public sealed record CheckRow(string Name, string Detail, string Glyph, string Tone)
{
    public static CheckRow From(PreflightCheck c) =>
        c.State switch
        {
            CheckState.Ok => new CheckRow(c.Name, c.Detail, "✓", "ok"),
            CheckState.Warn => new CheckRow(c.Name, c.Detail, "!", "warn"),
            _ => new CheckRow(c.Name, c.Detail, "✕", "fail"),
        };

    public bool IsOk => Tone == "ok";
    public bool IsWarn => Tone == "warn";
    public bool IsFail => Tone == "fail";
}

public partial class InstallViewModel : ViewModelBase
{
    public ObservableCollection<FileProgressRow> FileRows { get; } = [];
    public ObservableCollection<CheckRow> Checks { get; } = [];
    public ObservableCollection<PhaseRow> Phases { get; } = [];
    public ObservableCollection<ManualDownload> ManualDownloads { get; } = [];

    [ObservableProperty]
    public partial string InstallDir { get; set; }

    [ObservableProperty]
    public partial string DownloadDir { get; set; }

    // The Settings page edits the same two paths; the in-memory AppSettings object is the
    // single source of truth, updated on every keystroke so neither page can clobber the
    // other with stale values (disk persistence still happens on explicit actions).
    public ObservableCollection<ResolutionOption> Resolutions { get; } = [];

    [ObservableProperty]
    public partial ResolutionOption? SelectedResolution { get; set; }

    partial void OnSelectedResolutionChanged(ResolutionOption? value) =>
        _settings.Settings.PreferredResolution = value?.Value;

    partial void OnInstallDirChanged(string value) => _settings.Settings.InstallDir = value;

    partial void OnDownloadDirChanged(string value) => _settings.Settings.DownloadDir = value;

    /// <summary>Re-sync from settings when the page is shown (the other page may have edited them).</summary>
    public void ReloadDirs()
    {
        InstallDir = _settings.Settings.InstallDir;
        DownloadDir = _settings.Settings.DownloadDir;
    }

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    public partial bool IsBusy { get; set; }

    /// <summary>The primary button is dead while a run owns the app.</summary>
    public bool CanInstall => !IsBusy;

    public InstallViewModel(
        OperationRunner runner,
        SettingsService settings,
        LogService log,
        EngineProgress engineProgress,
        InstallOrchestrator orchestrator,
        PreflightService preflightService,
        NexusOAuthService oauth,
        NexusTokenStore tokenStore,
        JackifyEngineRunner engineRunner,
        DisplayCatalog displayCatalog
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

        // Offered before the run so a first install lands on the right resolution, but the
        // write happens after the engine finishes — the profiles do not exist until then.
        var storedResolution = settings.Settings.PreferredResolution;
        foreach (var option in ResolutionOption.Build(displayCatalog.Choices(), storedResolution))
        {
            Resolutions.Add(option);
        }
        SelectedResolution = ResolutionOption.Select(Resolutions, storedResolution);

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
                Phases[(int)change.Phase].Apply(change.State, change.Detail);
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

        runner.Started += _ => Dispatcher.UIThread.Post(() => IsBusy = true);
        runner.Completed += (_, result) =>
            Dispatcher.UIThread.Post(() =>
            {
                IsBusy = false;
                // The orchestrator may have signed the user in mid-run.
                RefreshAuthStatus();
                // Rows still marked running were interrupted: failed runs mark them failed,
                // a cancelled run returns them to pending rather than spinning forever.
                foreach (var row in Phases.Where(r => r.IsRunning))
                {
                    if (result.Outcome == OperationOutcome.Failed)
                    {
                        row.Apply(Services.Steam.StepState.Failed, "");
                    }
                    else
                    {
                        row.Reset();
                    }
                }
            });

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
                p.Reset();
            }
        });
        _rowLookup.Clear();

        await _runner.RunAsync("LoreRim install", (_, ct) => _orchestrator.RunAsync(ct));
    }

    [RelayCommand]
    private async Task BrowseInstallDirAsync()
    {
        if (await FolderPicker.PickFolderAsync("Choose the LoreRim install folder", InstallDir) is { } picked)
        {
            InstallDir = picked;
            await PersistDirsAsync();
            await RefreshChecksAsync();
        }
    }

    [RelayCommand]
    private async Task BrowseDownloadDirAsync()
    {
        if (await FolderPicker.PickFolderAsync("Choose the downloads folder", DownloadDir) is { } picked)
        {
            DownloadDir = picked;
            await PersistDirsAsync();
            await RefreshChecksAsync();
        }
    }

    [RelayCommand]
    private async Task RunPreflightAsync()
    {
        if (_runner.IsBusy)
        {
            return;
        }
        await PersistDirsAsync();
        await RefreshChecksAsync();
    }

    /// <summary>
    /// Runs preflight and fills the checklist. Called on demand and once when the page is
    /// first shown, so the user sees their system state without having to ask for it.
    /// </summary>
    public async Task RefreshChecksAsync()
    {
        if (_checking)
        {
            return;
        }
        _checking = true;
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
                    Checks.Add(CheckRow.From(c));
                }
                var failed = checks.Count(c => c.State == CheckState.Fail);
                var warned = checks.Count(c => c.State == CheckState.Warn);
                CheckResultText = failed > 0
                    ? $"{failed} blocking issue{(failed == 1 ? "" : "s")} — fix before installing."
                    : warned > 0
                        ? $"Ready, with {warned} thing{(warned == 1 ? "" : "s")} worth reading."
                        : "Everything checks out.";
            });
        }
        catch (Exception e)
        {
            CheckResultText = $"Preflight error: {e.Message}";
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Kick off the first check when the page appears; failures surface in the list.</summary>
    public void EnsureCheckedOnce()
    {
        if (_checkedOnce)
        {
            return;
        }
        _checkedOnce = true;
        _ = RefreshChecksAsync();
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
    private void OpenUrl(string? url)
    {
        // The URL comes from engine stdout (untrusted): only ever hand the browser
        // http(s), never arbitrary URI schemes with their own local handlers.
        if (!Services.SafeUrl.TryOpenInBrowser(url))
        {
            _log.Append($"Refusing to open non-web URL: {url}");
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

    private Task PersistDirsAsync() => _settings.SaveAsync();

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
    private bool _checking;
    private bool _checkedOnce;
}
