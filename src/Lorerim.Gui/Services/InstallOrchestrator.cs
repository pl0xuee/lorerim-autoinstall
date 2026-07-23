using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lorerim.Gui.Models;
using Lorerim.Gui.Services.Engine;
using Lorerim.Gui.Services.Modlist;
using Lorerim.Gui.Services.Nexus;
using Lorerim.Gui.Services.Steam;

namespace Lorerim.Gui.Services;

public enum InstallPhase
{
    Preflight,
    Auth,
    ResolveModlist,
    EngineInstall,
    VerifyInstall,
    SteamSetup,
    Done,
}

public sealed record InstallPhaseChange(InstallPhase Phase, StepState State, string Detail);

/// <summary>
/// The one-click state machine: preflight → auth → resolve → engine install → verify →
/// Steam setup. Runs inside OperationRunner (single operation, cancellable).
/// </summary>
public class InstallOrchestrator(
    SettingsService settingsService,
    PreflightService preflight,
    NexusOAuthService oauth,
    NexusTokenStore tokenStore,
    ModlistResolverService modlistResolver,
    JackifyEngineRunner engine,
    SteamLocator steamLocator,
    CompatToolCatalog compatTools,
    SteamRuntimeCatalog steamRuntimes,
    SteamIntegrationService steamIntegration,
    ModFixupService modFixups,
    LogService log
)
{
    public const string SteamShortcutName = "LoreRim";

    public event Action<InstallPhaseChange>? PhaseChanged;

    /// <summary>Per-step reporter for the Steam-integration sub-pipeline.</summary>
    public event Action<int, StepState, string>? SteamStepChanged;

    public async Task RunAsync(CancellationToken ct)
    {
        var settings = settingsService.Settings;
        var installDir = Path.GetFullPath(ExpandHome(settings.InstallDir));
        var downloadDir = Path.GetFullPath(ExpandHome(settings.DownloadDir));

        // ---- Preflight ----
        Report(InstallPhase.Preflight, StepState.Running);
        var checks = await preflight.RunAsync(installDir, downloadDir, ct);
        foreach (var c in checks)
        {
            log.Append($"preflight: {c.Name}: {c.State} — {c.Detail}");
        }
        var failures = checks.Where(c => c.State == CheckState.Fail).ToList();
        if (failures.Count > 0)
        {
            var summary = string.Join("\n", failures.Select(f => $"• {f.Name}: {f.Detail}"));
            Report(InstallPhase.Preflight, StepState.Failed, summary);
            throw new InvalidOperationException($"Preflight failed:\n{summary}");
        }
        // Build the Steam context now so a broken Steam setup fails here, not 250GB later.
        var steam =
            steamLocator.Locate()
            ?? throw new InvalidOperationException("Steam disappeared between checks");
        var tool =
            PickCompatTool(steam)
            ?? throw new InvalidOperationException("No Proton compatibility tool found");
        Report(InstallPhase.Preflight, StepState.Ok);

        // ---- Auth ----
        Report(InstallPhase.Auth, StepState.Running);
        var token = await oauth.GetFreshTokenAsync(ct);
        if (token is null && string.IsNullOrEmpty(settings.NexusApiKey))
        {
            token = await oauth.AuthorizeAsync(ct);
        }
        if (token is null && string.IsNullOrEmpty(settings.NexusApiKey))
        {
            Report(InstallPhase.Auth, StepState.Failed, "Nexus sign-in did not complete");
            throw new InvalidOperationException(
                "Nexus sign-in did not complete. Sign in (or paste an API key in Settings) and retry."
            );
        }
        Report(InstallPhase.Auth, StepState.Ok, token?.UserName ?? "API key");

        // ---- Resolve modlist ----
        Report(InstallPhase.ResolveModlist, StepState.Running);
        string? wabbajackFile = null;
        string? machineUrl = null;
        if (!string.IsNullOrEmpty(settings.WabbajackFilePath))
        {
            if (!File.Exists(settings.WabbajackFilePath))
            {
                Report(InstallPhase.ResolveModlist, StepState.Failed, "file missing");
                throw new FileNotFoundException(
                    $"The configured .wabbajack file does not exist: {settings.WabbajackFilePath}"
                );
            }
            wabbajackFile = settings.WabbajackFilePath;
            Report(InstallPhase.ResolveModlist, StepState.Ok, Path.GetFileName(wabbajackFile));
        }
        else if (!string.IsNullOrEmpty(settings.MachineUrlOverride))
        {
            machineUrl = settings.MachineUrlOverride;
            Report(InstallPhase.ResolveModlist, StepState.Ok, machineUrl);
        }
        else
        {
            var info = await modlistResolver.ResolveLorerimAsync(ct);
            machineUrl = info.MachineUrl;
            // Re-check space against real catalog numbers (10% headroom) using the same
            // shared-volume-aware logic as preflight — separate per-dir checks can both pass
            // while the sum runs a shared disk dry.
            var spaceChecks = PreflightService.SpaceChecks(
                installDir,
                downloadDir,
                Headroom(info.DownloadSizeBytes, PreflightService.RequiredDownloadBytes),
                Headroom(info.InstallSizeBytes, PreflightService.RequiredInstallBytes)
            );
            var spaceFailure = spaceChecks.FirstOrDefault(c => c.State == CheckState.Fail);
            if (spaceFailure is not null)
            {
                Report(InstallPhase.ResolveModlist, StepState.Failed, spaceFailure.Detail);
                throw new InvalidOperationException(
                    $"{spaceFailure.Name}: {spaceFailure.Detail}"
                );
            }
            Report(InstallPhase.ResolveModlist, StepState.Ok, $"{info.Title} ({info.MachineUrl})");
        }

        // ---- Engine install ----
        Report(InstallPhase.EngineInstall, StepState.Running);
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(downloadDir);
        var authEnv = tokenStore.BuildEngineAuthEnv(settings.NexusApiKey);
        try
        {
            await engine.InstallAsync(
                new EngineInstallRequest(wabbajackFile, machineUrl, installDir, downloadDir, authEnv),
                ct
            );
        }
        finally
        {
            // The engine may have rotated our refresh token even on failure/cancel.
            tokenStore.ApplyWriteback();
        }
        Report(InstallPhase.EngineInstall, StepState.Ok);

        // ---- Verify ----
        Report(InstallPhase.VerifyInstall, StepState.Running);
        var mo2Exe = Path.Join(installDir, "ModOrganizer.exe");
        if (!File.Exists(mo2Exe))
        {
            var found = Directory
                .EnumerateFiles(installDir, "ModOrganizer.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (found is null)
            {
                Report(InstallPhase.VerifyInstall, StepState.Failed, "ModOrganizer.exe not found");
                throw new InvalidOperationException(
                    $"Install finished but ModOrganizer.exe was not found under {installDir}."
                );
            }
            mo2Exe = found;
        }
        Report(InstallPhase.VerifyInstall, StepState.Ok, mo2Exe);

        // Before Steam setup, not after: the profiles exist the moment the engine finishes,
        // and a Steam step failing should not cost the user the resolution they picked.
        ApplyResolutionPreference(Path.GetDirectoryName(mo2Exe)!);

        // ---- Steam setup ----
        if (settings.SetupSteamAfterInstall)
        {
            Report(InstallPhase.SteamSetup, StepState.Running);
            var ctx = new SteamSetupContext(
                steam,
                tool,
                SteamShortcutName,
                mo2Exe,
                SteamIntegrationService.BuildLaunchOptions([installDir, downloadDir])
            );
            await steamIntegration.RunAsync(
                ctx,
                (i, s, d) => SteamStepChanged?.Invoke(i, s, d),
                ct
            );
            Report(InstallPhase.SteamSetup, StepState.Ok);
        }
        else
        {
            // Steam setup normally ends with the compatibility fixes; without it they would
            // be skipped and the modlist would crash on launch.
            await modFixups.ApplyAsync(Path.GetDirectoryName(mo2Exe)!, ct);
        }

        Report(InstallPhase.Done, StepState.Ok);
    }

    /// <summary>
    /// Writes the chosen render resolution into every profile. A failure here is logged and
    /// not thrown: a finished install with the modlist's own resolution is still a working
    /// install, and the user can re-apply from Settings.
    /// </summary>
    private void ApplyResolutionPreference(string installDir)
    {
        var preference = settingsService.Settings.PreferredResolution;
        if (preference is null)
        {
            return;
        }
        try
        {
            if (SkyrimResolutionService.ApplyPreference(installDir, preference))
            {
                log.Append($"Resolution: set to {preference} in every profile.");
            }
            else
            {
                log.Append($"Resolution: '{preference}' is not a resolution; left unchanged.");
            }
        }
        catch (IOException ex)
        {
            log.Append($"Resolution: could not be applied — {ex.Message}");
        }
    }

    private CompatTool? PickCompatTool(SteamInstallation steam)
    {
        var tools = compatTools.Scan(steam.Root);
        var runtimeInstalled = steamRuntimes.AvailabilityFor(steam.Root);
        var preferred = settingsService.Settings.PreferredProtonInternalName;

        if (
            !string.IsNullOrEmpty(preferred)
            && !tools.Any(t => t.InternalName.Equals(preferred, StringComparison.OrdinalIgnoreCase))
        )
        {
            log.Append($"Preferred Proton '{preferred}' not found; falling back to best available.");
        }

        var selection = LorerimProton.Select(tools, runtimeInstalled, preferred);
        if (selection.SubstitutedFor is { } replaced && selection.Tool is { } chosen)
        {
            var cause = LorerimProton.SubstitutionReason(replaced, runtimeInstalled);
            log.Append($"{replaced.DisplayName} {cause}; using {chosen.DisplayName} instead.");
        }
        return selection.Tool;
    }

    private static long Headroom(long? catalogBytes, long fallback) =>
        catalogBytes is { } b ? (long)(b * 1.1) : fallback;

    private static string ExpandHome(string path) => AppSettings.ExpandHome(path);

    private void Report(InstallPhase phase, StepState state, string detail = "") =>
        PhaseChanged?.Invoke(new InstallPhaseChange(phase, state, detail));
}
