using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Lorerim.Gui.Services.Steam;

public enum StepState
{
    Running,
    Ok,
    Failed,
}

public sealed record SteamSetupContext(
    SteamInstallation Steam,
    CompatTool Tool,
    string AppName,
    string Mo2Exe,
    string LaunchOptions,
    // When true, an existing shortcut for this AppName is left untouched (an install re-run
    // must not clobber or duplicate a user's Steam entry). The standalone Steam Setup page
    // leaves this false so it can (re)write or repair the entry on demand.
    bool PreserveExistingShortcut = false
)
{
    public string StartDir => Path.GetDirectoryName(Mo2Exe)!;
}

/// <summary>
/// The full Jackify-style pipeline as one reusable unit, shared by the Steam page and the
/// one-click install: shutdown Steam → write shortcuts.vdf + CompatToolMapping → restart
/// Steam → create Proton prefix → install prerequisites via protontricks.
/// Step indices reported via <c>report(stepIndex, state, detail)</c> match <see cref="StepNames"/>.
/// </summary>
public class SteamIntegrationService(
    ShortcutsVdfService shortcutsVdf,
    ConfigVdfService configVdf,
    SteamProcessService steamProcess,
    ProtonPrefixService prefixService,
    ProtontricksService protontricks,
    SteamGridArtService gridArt,
    Modlist.ModFixupService modFixups,
    LogService log
)
{
    public static readonly string[] StepNames =
    [
        "Shut down Steam",
        "Write Steam shortcut + compatibility tool",
        "Restart Steam",
        "Create Proton prefix",
        "Install prerequisites (protontricks)",
        "Apply Linux compatibility fixes",
    ];

    public async Task RunAsync(
        SteamSetupContext ctx,
        Action<int, StepState, string>? report = null,
        CancellationToken ct = default
    )
    {
        await RunWithFixupsAsync(
            () => RunSteamStepsAsync(ctx, report, ct),
            () => Step(5, () => modFixups.ApplyAsync(ctx.StartDir, ct), report),
            message => log.Append($"Compatibility fixes could not be applied: {message}"),
            ct
        );
    }

    private async Task RunSteamStepsAsync(
        SteamSetupContext ctx,
        Action<int, StepState, string>? report,
        CancellationToken ct
    )
    {
        SteamShortcut shortcut = null!;
        await Step(0, () => steamProcess.ShutdownAsync(ct), report);
        await Step(
            1,
            () =>
            {
                var existing = ctx.PreserveExistingShortcut
                    ? shortcutsVdf.Find(ctx.Steam, ctx.AppName)
                    : null;
                if (existing is not null)
                {
                    // Leave the user's entry and artwork exactly as they are; reuse its appid so
                    // the prefix/protontricks/fixes below still target it. One exception: if the
                    // compatibility-tool mapping is missing (e.g. a first run wrote the shortcut
                    // but failed before setting it), restore it — without it Steam launches
                    // ModOrganizer.exe with no Proton and it never runs. An existing mapping is
                    // left untouched so a user's manual Proton choice survives.
                    shortcut = existing;
                    if (configVdf.GetCompatTool(ctx.Steam, existing.UnsignedAppId) is null)
                    {
                        configVdf.SetCompatTool(ctx.Steam, existing.UnsignedAppId, ctx.Tool.InternalName);
                        log.Append(
                            $"Existing Steam shortcut '{ctx.AppName}' found (appid {existing.SignedAppId}); left untouched, restored missing compatibility tool → {ctx.Tool.InternalName}."
                        );
                    }
                    else
                    {
                        log.Append(
                            $"Existing Steam shortcut '{ctx.AppName}' found (appid {existing.SignedAppId}, compat key {existing.UnsignedAppId}); leaving it untouched."
                        );
                    }
                    return Task.CompletedTask;
                }
                shortcut = shortcutsVdf.Upsert(
                    ctx.Steam,
                    ctx.AppName,
                    ctx.Mo2Exe,
                    ctx.StartDir,
                    ctx.LaunchOptions,
                    gridArt.InstallIcon(ctx.Steam)
                );
                configVdf.SetCompatTool(ctx.Steam, shortcut.UnsignedAppId, ctx.Tool.InternalName);
                gridArt.InstallGridArt(ctx.Steam, shortcut.UnsignedAppId);
                log.Append(
                    $"Shortcut '{ctx.AppName}' appid {shortcut.SignedAppId} (compat key {shortcut.UnsignedAppId}) → {ctx.Tool.InternalName}"
                );
                return Task.CompletedTask;
            },
            report
        );
        await Step(2, () => steamProcess.StartAndWaitAsync(ct), report);
        await Step(3, () => prefixService.CreateAsync(ctx.Steam, ctx.Tool, shortcut.UnsignedAppId, ct), report);
        await Step(
            4,
            () => protontricks.InstallComponentsAsync(
                shortcut.UnsignedAppId,
                Path.Join(ctx.Steam.CompatDataDir, shortcut.UnsignedAppId.ToString(), "pfx"),
                ct
            ),
            report
        );
    }

    /// <summary>
    /// Runs the Steam steps, then the modlist compatibility pass — and runs that pass even
    /// when a Steam step fails. By that point the modlist is fully installed, so skipping it
    /// leaves an install that crashes on launch over a Steam problem that has nothing to do
    /// with it. A cancelled run is the one case that skips it: the user asked to stop.
    /// </summary>
    internal static async Task RunWithFixupsAsync(
        Func<Task> steamSteps,
        Func<Task> applyFixups,
        Action<string> onFixupError,
        CancellationToken ct
    )
    {
        try
        {
            await steamSteps();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Logged rather than thrown: the Steam failure is the one worth surfacing, and
            // replacing it with a fixup error would hide what actually went wrong.
            try
            {
                await applyFixups();
            }
            catch (Exception fixupError)
            {
                onFixupError(fixupError.Message);
            }
            throw;
        }
        await applyFixups();
    }

    /// <summary>STEAM_COMPAT_MOUNTS for any LoreRim path outside the home mount, always ending in %command%.</summary>
    public static string BuildLaunchOptions(IEnumerable<string> paths)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var homePrefix = home.EndsWith(Path.DirectorySeparatorChar) ? home : home + Path.DirectorySeparatorChar;
        var mounts = paths
            .Select(Path.GetFullPath)
            // Trailing separator so /home/bob doesn't match /home/bob2; equal-to-home also counts as inside.
            .Where(path => !path.Equals(home, StringComparison.Ordinal)
                && !path.StartsWith(homePrefix, StringComparison.Ordinal))
            .Distinct()
            .ToList();
        return mounts.Count > 0
            ? $"STEAM_COMPAT_MOUNTS=\"{string.Join(':', mounts)}\" %command%"
            : "%command%";
    }

    private static async Task Step(int index, Func<Task> action, Action<int, StepState, string>? report)
    {
        report?.Invoke(index, StepState.Running, "");
        try
        {
            await action();
            report?.Invoke(index, StepState.Ok, "");
        }
        catch (Exception e)
        {
            report?.Invoke(index, StepState.Failed, e.Message);
            throw;
        }
    }
}
