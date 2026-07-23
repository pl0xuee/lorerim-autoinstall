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
    string LaunchOptions
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
    ];

    public async Task RunAsync(
        SteamSetupContext ctx,
        Action<int, StepState, string>? report = null,
        CancellationToken ct = default
    )
    {
        SteamShortcut shortcut = null!;
        await Step(0, () => steamProcess.ShutdownAsync(ct), report);
        await Step(
            1,
            () =>
            {
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
