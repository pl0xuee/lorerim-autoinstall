using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lorerim.Gui.Models;
using Lorerim.Gui.Services;
using Lorerim.Gui.Services.Nexus;
using Lorerim.Gui.Services.Steam;

namespace Lorerim.Gui.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string InstallDir { get; set; }

    [ObservableProperty]
    public partial string DownloadDir { get; set; }

    [ObservableProperty]
    public partial string WabbajackFilePath { get; set; }

    [ObservableProperty]
    public partial string MachineUrlOverride { get; set; }

    [ObservableProperty]
    public partial bool SetupSteamAfterInstall { get; set; }

    [ObservableProperty]
    public partial string NexusApiKey { get; set; }

    [ObservableProperty]
    public partial string ApiKeyStatus { get; set; } = "";

    [ObservableProperty]
    public partial string OAuthStatus { get; set; } = "";

    public ObservableCollection<CompatTool> CompatTools { get; } = [];

    [ObservableProperty]
    public partial CompatTool? SelectedTool { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial string AppVersionText { get; set; } =
        $"Version {AppUpdateService.CurrentVersion}";

    [ObservableProperty]
    public partial string UpdateStatusText { get; set; } = "";

    [ObservableProperty]
    public partial string? UpdateUrl { get; set; }

    [ObservableProperty]
    public partial bool CanInstallUpdate { get; set; }

    public SettingsViewModel(
        SettingsService settings,
        LogService log,
        AppUpdateService appUpdate,
        OperationRunner runner,
        NexusTokenStore tokenStore,
        SteamLocator steamLocator,
        CompatToolCatalog compatToolCatalog
    )
    {
        _settings = settings;
        _log = log;
        _appUpdate = appUpdate;
        _runner = runner;
        _tokenStore = tokenStore;
        _steamLocator = steamLocator;
        _compatToolCatalog = compatToolCatalog;

        var s = settings.Settings;
        InstallDir = s.InstallDir;
        DownloadDir = s.DownloadDir;
        WabbajackFilePath = s.WabbajackFilePath ?? "";
        MachineUrlOverride = s.MachineUrlOverride ?? "";
        SetupSteamAfterInstall = s.SetupSteamAfterInstall;
        NexusApiKey = s.NexusApiKey ?? "";
        StatusText = $"Settings file: {AppSettings.SettingsPath}";
        RefreshProtonList();
        RefreshOAuthStatus();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var s = _settings.Settings;
        s.InstallDir = InstallDir;
        s.DownloadDir = DownloadDir;
        s.WabbajackFilePath = NullIfEmpty(WabbajackFilePath);
        s.MachineUrlOverride = NullIfEmpty(MachineUrlOverride);
        s.SetupSteamAfterInstall = SetupSteamAfterInstall;
        s.NexusApiKey = NullIfEmpty(NexusApiKey);
        s.PreferredProtonInternalName = SelectedTool?.InternalName;
        await _settings.SaveAsync();
        StatusText = $"Saved to {AppSettings.SettingsPath}";
        _log.Append("Settings saved");
    }

    [RelayCommand]
    private void RefreshProtonList()
    {
        var previous = SelectedTool?.InternalName ?? _settings.Settings.PreferredProtonInternalName;
        CompatTools.Clear();
        foreach (var tool in _compatToolCatalog.Scan(_steamLocator.Locate()?.Root))
        {
            CompatTools.Add(tool);
        }
        SelectedTool =
            CompatTools.FirstOrDefault(t => t.InternalName == previous)
            ?? CompatTools.FirstOrDefault();
    }

    /// <summary>Validate a pasted API key against the Nexus users/validate endpoint.</summary>
    [RelayCommand]
    private async Task ValidateApiKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(NexusApiKey))
        {
            ApiKeyStatus = "Paste an API key first (nexusmods.com → Settings → API Keys).";
            return;
        }
        ApiKeyStatus = "Validating…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                "https://api.nexusmods.com/v1/users/validate.json"
            );
            req.Headers.Add("apikey", NexusApiKey.Trim());
            using var response = await http.SendAsync(req);
            if (!response.IsSuccessStatusCode)
            {
                ApiKeyStatus = $"Invalid key (HTTP {(int)response.StatusCode}).";
                return;
            }
            using var doc = System.Text.Json.JsonDocument.Parse(
                await response.Content.ReadAsStringAsync()
            );
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            var premium =
                doc.RootElement.TryGetProperty("is_premium", out var pr) && pr.GetBoolean();
            ApiKeyStatus = $"Valid — {name}{(premium ? " (Premium)" : " (free: downloads need manual clicks)")}";
            NexusApiKey = NexusApiKey.Trim();
            await SaveAsync();
        }
        catch (Exception e)
        {
            ApiKeyStatus = $"Validation failed: {e.Message}";
        }
    }

    [RelayCommand]
    private void SignOut()
    {
        _tokenStore.Clear();
        RefreshOAuthStatus();
        _log.Append("Signed out of Nexus (OAuth token deleted).");
    }

    private void RefreshOAuthStatus()
    {
        var token = _tokenStore.Load();
        OAuthStatus = token?.AccessToken is null
            ? "Not signed in — use the Install page to sign in via browser."
            : $"Signed in{(token.UserName is null ? "" : $" as {token.UserName}")}{(token.IsPremium ? " (Premium)" : "")}";
    }

    [RelayCommand]
    private async Task CheckAppUpdateAsync()
    {
        UpdateStatusText = "Checking…";
        UpdateUrl = null;
        CanInstallUpdate = false;
        _pendingUpdate = null;
        try
        {
            var result = await _appUpdate.CheckAsync();
            if (result.UpdateAvailable)
            {
                _pendingUpdate = result;
                UpdateUrl = result.ReleaseUrl;
                // Self-install needs the AppImage path (from $APPIMAGE) and a downloadable
                // asset; otherwise leave the release page as the manual fallback.
                CanInstallUpdate =
                    result.AssetUrl is not null && AppUpdateService.InstalledAppImagePath is not null;
                UpdateStatusText = CanInstallUpdate
                    ? $"New release available: {result.LatestTag}"
                    : $"New release available: {result.LatestTag} — download it from the release page";
            }
            else
            {
                UpdateStatusText = $"Up to date ({result.CurrentVersion})";
            }
        }
        catch (Exception e)
        {
            UpdateStatusText = $"Check failed: {e.Message}";
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate?.AssetUrl is not { } assetUrl)
        {
            return;
        }
        // Don't tear the app down mid-operation: a 600GB install shares this process.
        if (_runner.IsBusy)
        {
            UpdateStatusText = "Finish or cancel the running operation before updating.";
            return;
        }
        CanInstallUpdate = false;
        try
        {
            var progress = new Progress<double>(p =>
                UpdateStatusText = $"Downloading {_pendingUpdate.LatestTag}… {p:P0}"
            );
            var updated = await _appUpdate.DownloadAndInstallAsync(
                assetUrl,
                _pendingUpdate.AssetSha256,
                progress
            );
            _log.Append($"App updated to {_pendingUpdate.LatestTag}, restarting");
            UpdateStatusText = "Restarting…";
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo { FileName = updated, UseShellExecute = false }
            );
            if (
                Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            )
            {
                desktop.Shutdown();
            }
        }
        catch (Exception e)
        {
            CanInstallUpdate = true;
            UpdateStatusText = $"Update failed: {e.Message}";
            _log.Append($"App update failed: {e.Message}");
        }
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        if (UpdateUrl is null)
        {
            return;
        }
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo { FileName = UpdateUrl, UseShellExecute = true }
        );
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private readonly SettingsService _settings;
    private readonly LogService _log;
    private readonly AppUpdateService _appUpdate;
    private readonly OperationRunner _runner;
    private readonly NexusTokenStore _tokenStore;
    private readonly SteamLocator _steamLocator;
    private readonly CompatToolCatalog _compatToolCatalog;
    private AppUpdateCheck? _pendingUpdate;
}
