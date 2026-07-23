using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lorerim.Gui.Models;
using Lorerim.Gui.Services;
using Lorerim.Gui.Services.Display;
using Lorerim.Gui.Services.Modlist;
using Lorerim.Gui.Services.Nexus;
using Lorerim.Gui.Services.Steam;

namespace Lorerim.Gui.ViewModels;

/// <summary>
/// One entry in the resolution picker. <see cref="Value"/> is null for "leave the modlist's
/// own resolution alone", which is the default so an install nobody configured is untouched.
/// </summary>
public sealed record ResolutionOption(string? Value, string Label)
{
    public override string ToString() => Label;

    /// <summary>
    /// The picker's entries, shared by the install page and Settings so the two cannot drift.
    /// "Leave alone" leads, then every detected resolution.
    /// </summary>
    public static List<ResolutionOption> Build(
        IReadOnlyList<ResolutionChoice> choices,
        string? stored,
        bool primaryIsGuess = false
    )
    {
        List<ResolutionOption> options = [new(null, "Leave as the modlist ships")];
        // Without xrandr the largest output is assumed to be primary, and saying "primary"
        // outright would state something the app has not actually established.
        var primaryLabel = primaryIsGuess ? "primary (assumed), native" : "primary, native";
        foreach (var choice in choices)
        {
            var displays = string.Join(", ", choice.Displays);
            options.Add(
                new ResolutionOption(
                    choice.Mode.ToString(),
                    $"{choice.Mode}  ({(choice.IsPrimaryNative ? $"{displays} — {primaryLabel}" : displays)})"
                )
            );
        }
        // Unplugging the monitor a resolution was chosen for must not silently rewrite the
        // setting to "leave alone" the next time this list is built.
        if (stored is not null && options.All(o => o.Value != stored))
        {
            options.Add(new ResolutionOption(stored, $"{stored}  (not offered by any display)"));
        }
        return options;
    }

    public static ResolutionOption Select(IReadOnlyList<ResolutionOption> options, string? stored) =>
        options.FirstOrDefault(o => o.Value == stored) ?? options[0];
}

public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string InstallDir { get; set; }

    [ObservableProperty]
    public partial string DownloadDir { get; set; }

    // Shared with the Install page through the in-memory AppSettings object — sync on every
    // change so switching pages never resurrects stale paths.
    partial void OnInstallDirChanged(string value) => _settings.Settings.InstallDir = value;

    partial void OnDownloadDirChanged(string value) => _settings.Settings.DownloadDir = value;

    /// <summary>Re-sync from settings when the page is shown.</summary>
    public void ReloadShared()
    {
        InstallDir = _settings.Settings.InstallDir;
        DownloadDir = _settings.Settings.DownloadDir;
    }

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

    [ObservableProperty]
    public partial bool IsSignedIn { get; set; }

    public ObservableCollection<ResolutionOption> Resolutions { get; } = [];

    [ObservableProperty]
    public partial ResolutionOption? SelectedResolution { get; set; }

    // Unlike the Proton pin, a null selection is a deliberate choice here — "leave as the
    // modlist ships" — so it counts as touched too. Only programmatic refreshes are excluded.
    partial void OnSelectedResolutionChanged(ResolutionOption? value)
    {
        if (!_suppressResolutionDirty)
        {
            _resolutionTouched = true;
        }
    }

    [ObservableProperty]
    public partial string ResolutionStatus { get; set; } = "";

    public ObservableCollection<CompatTool> CompatTools { get; } = [];

    [ObservableProperty]
    public partial CompatTool? SelectedTool { get; set; }

    // Only a deliberate ComboBox choice becomes a persisted pin. Programmatic selection
    // (ctor, rescans) must not: pinning whatever was auto-picked would silently override
    // the GE-Proton10-34 requirement the next time the user validates an API key or
    // browses a folder (both of which save settings).
    partial void OnSelectedToolChanged(CompatTool? value)
    {
        if (!_suppressProtonDirty && value is not null)
        {
            _protonTouched = true;
        }
    }

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
        CompatToolCatalog compatToolCatalog,
        DisplayCatalog displayCatalog
    )
    {
        _displayCatalog = displayCatalog;
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
        RefreshResolutions();
        RefreshOAuthStatus();
    }

    /// <summary>
    /// Rebuilds the resolution list from the connected displays and reports how the install's
    /// current setting compares. Detection failing is not an error: the list falls back to
    /// "leave alone" plus whatever the install already uses.
    /// </summary>
    [RelayCommand]
    private void RefreshResolutions()
    {
        var stored = _settings.Settings.PreferredResolution;
        var choices = _displayCatalog.Choices();

        Resolutions.Clear();
        foreach (var option in ResolutionOption.Build(choices, stored, _displayCatalog.PrimaryIsGuess))
        {
            Resolutions.Add(option);
        }
        _suppressResolutionDirty = true;
        try
        {
            SelectedResolution = ResolutionOption.Select(Resolutions, stored);
        }
        finally
        {
            _suppressResolutionDirty = false;
        }
        ResolutionStatus = DescribeResolution(choices);
    }

    /// <summary>Writes the picked resolution into an existing install without re-running one.</summary>
    [RelayCommand]
    private async Task ApplyResolutionAsync()
    {
        // This page is built once at startup, so a resolution chosen on the install page since
        // then is not reflected here. Re-sync unless the user has made a choice of their own,
        // which must not be discarded.
        if (!_resolutionTouched)
        {
            RefreshResolutions();
        }

        var installDir = AppSettings.ExpandHome(InstallDir);
        if (!Directory.Exists(Path.Join(installDir, "profiles")))
        {
            ResolutionStatus = "No install found at the install folder yet — install first.";
            return;
        }
        await SaveAsync();
        try
        {
            if (SkyrimResolutionService.ApplyPreference(installDir, SelectedResolution?.Value))
            {
                _log.Append($"Resolution: set to {SelectedResolution!.Value} in every profile.");
            }
            else
            {
                ResolutionStatus = "Nothing to apply — the modlist's own resolution is kept.";
                return;
            }
        }
        catch (IOException ex)
        {
            ResolutionStatus = $"Could not write the resolution: {ex.Message}";
            return;
        }
        RefreshResolutions();
    }

    /// <summary>Names the mismatch that makes this setting worth having.</summary>
    private string DescribeResolution(IReadOnlyList<ResolutionChoice> choices)
    {
        var installDir = AppSettings.ExpandHome(InstallDir);
        if (SkyrimResolutionService.Read(installDir) is not { } current)
        {
            return choices.Count == 0
                ? "No displays detected and no install to read."
                : "No install to read a resolution from yet.";
        }
        var currentText = $"{current.Width}x{current.Height}";
        if (choices.Count == 0)
        {
            return $"Install is set to {currentText}. No displays detected.";
        }
        var native = choices.Where(c => c.IsPrimaryNative).Select(c => c.Mode.ToString()).FirstOrDefault();
        var matches = choices.Any(c => c.Mode.ToString() == currentText);
        return matches
            ? $"Install is set to {currentText}."
            : $"Install is set to {currentText}, which no connected display offers"
                + (native is null ? "." : $" — primary is {native}.");
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
        // Guarded for the same reason as the Proton pin below: this page is a singleton built
        // at startup, so a stale selection would otherwise overwrite a resolution chosen on
        // the install page the next time any unrelated action here saves settings.
        if (_resolutionTouched)
        {
            s.PreferredResolution = SelectedResolution?.Value;
        }
        if (_protonTouched)
        {
            s.PreferredProtonInternalName = SelectedTool?.InternalName;
        }
        await _settings.SaveAsync();
        StatusText = $"Saved to {AppSettings.SettingsPath}";
        _log.Append("Settings saved");
    }

    [RelayCommand]
    private async Task BrowseInstallDirAsync()
    {
        if (await FolderPicker.PickFolderAsync("Choose the LoreRim install folder", InstallDir) is { } picked)
        {
            InstallDir = picked;
            await SaveAsync();
        }
    }

    [RelayCommand]
    private async Task BrowseDownloadDirAsync()
    {
        if (await FolderPicker.PickFolderAsync("Choose the downloads folder", DownloadDir) is { } picked)
        {
            DownloadDir = picked;
            await SaveAsync();
        }
    }

    [RelayCommand]
    private async Task BrowseWabbajackFileAsync()
    {
        if (await FolderPicker.PickWabbajackFileAsync(WabbajackFilePath) is { } picked)
        {
            WabbajackFilePath = picked;
            await SaveAsync();
        }
    }

    [RelayCommand]
    private void RefreshProtonList()
    {
        var previous = SelectedTool?.InternalName ?? _settings.Settings.PreferredProtonInternalName;
        _suppressProtonDirty = true;
        try
        {
            CompatTools.Clear();
            foreach (var tool in _compatToolCatalog.Scan(_steamLocator.Locate()?.Root))
            {
                CompatTools.Add(tool);
            }
            SelectedTool =
                CompatTools.FirstOrDefault(t => t.InternalName == previous)
                ?? CompatTools.FirstOrDefault();
        }
        finally
        {
            _suppressProtonDirty = false;
        }
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

    /// <summary>Re-read auth state when the page is shown (sign-in happens on the Install page).</summary>
    public void RefreshAuthState() => RefreshOAuthStatus();

    private void RefreshOAuthStatus()
    {
        var token = _tokenStore.Load();
        IsSignedIn = token?.AccessToken is not null;
        OAuthStatus = IsSignedIn
            ? $"Signed in{(token!.UserName is null ? "" : $" as {token.UserName}")}{(token.IsPremium ? " (Premium)" : "")}"
            : "Not signed in — use the Install page to sign in via browser.";
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
        if (!SafeUrl.TryOpenInBrowser(UpdateUrl))
        {
            _log.Append($"Refusing to open non-web URL: {UpdateUrl}");
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private readonly SettingsService _settings;
    private readonly LogService _log;
    private readonly AppUpdateService _appUpdate;
    private readonly OperationRunner _runner;
    private readonly NexusTokenStore _tokenStore;
    private readonly SteamLocator _steamLocator;
    private readonly CompatToolCatalog _compatToolCatalog;
    private readonly DisplayCatalog _displayCatalog;
    private AppUpdateCheck? _pendingUpdate;
    private bool _protonTouched;
    private bool _suppressProtonDirty;
    private bool _resolutionTouched;
    private bool _suppressResolutionDirty;
}
