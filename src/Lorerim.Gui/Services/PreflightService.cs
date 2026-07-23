using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lorerim.Gui.Services.Engine;
using Lorerim.Gui.Services.Steam;

namespace Lorerim.Gui.Services;

public enum CheckState
{
    Ok,
    Warn,
    Fail,
}

public sealed record PreflightCheck(string Name, CheckState State, string Detail);

/// <summary>
/// Fail-before-the-download-starts checks, shown as a checklist on the Install page.
/// LoreRim: ~250GB of downloads + ~330GB installed.
/// </summary>
public class PreflightService(
    SteamLocator steamLocator,
    SkyrimLocator skyrimLocator,
    ProtontricksService protontricks,
    CompatToolCatalog compatTools,
    JackifyEngineLocator engineLocator,
    JackifyEngineRunner engineRunner
)
{
    public const long RequiredDownloadBytes = 260L * 1024 * 1024 * 1024;
    public const long RequiredInstallBytes = 340L * 1024 * 1024 * 1024;

    public async Task<List<PreflightCheck>> RunAsync(
        string installDir,
        string downloadDir,
        CancellationToken ct
    )
    {
        var checks = new List<PreflightCheck>();

        var steam = steamLocator.Locate();
        checks.Add(
            steam is null
                ? new PreflightCheck(
                    "Steam",
                    CheckState.Fail,
                    "Native Steam not found (Flatpak/Snap Steam is not supported)."
                )
                : new PreflightCheck("Steam", CheckState.Ok, steam.Root)
        );

        var skyrim = steam is null ? null : skyrimLocator.Locate();
        checks.Add(
            skyrim switch
            {
                null => new PreflightCheck(
                    "Skyrim Special Edition",
                    CheckState.Fail,
                    "Not installed. Install Skyrim SE (Anniversary Edition, English) from Steam and launch it once."
                ),
                { FullyInstalled: false } => new PreflightCheck(
                    "Skyrim Special Edition",
                    CheckState.Warn,
                    $"Found at {skyrim.GameDir} but Steam reports it not fully installed — let the download finish first."
                ),
                _ => new PreflightCheck("Skyrim Special Edition", CheckState.Ok, skyrim.GameDir),
            }
        );

        var (ptOk, ptVersion) = await protontricks.IsAvailableAsync(ct);
        checks.Add(
            ptOk
                ? new PreflightCheck("protontricks", CheckState.Ok, ptVersion)
                : new PreflightCheck(
                    "protontricks",
                    CheckState.Fail,
                    "Not found. Install it from your distro's repositories (e.g. pacman -S protontricks)."
                )
        );

        var tools = compatTools.Scan(steam?.Root);
        checks.Add(
            tools.Count == 0
                ? new PreflightCheck(
                    "Proton",
                    CheckState.Fail,
                    "No Proton found in compatibilitytools.d. Install GE-Proton (e.g. with ProtonUp-Qt)."
                )
                : tools[0].InternalName.StartsWith("GE-Proton", StringComparison.OrdinalIgnoreCase)
                    ? new PreflightCheck("Proton", CheckState.Ok, tools[0].DisplayName)
                    : new PreflightCheck(
                        "Proton",
                        CheckState.Warn,
                        $"{tools[0].DisplayName} found; GE-Proton is recommended for LoreRim's ENB."
                    )
        );

        checks.Add(DiskCheck("Download space", downloadDir, RequiredDownloadBytes));
        checks.Add(DiskCheck("Install space", installDir, RequiredInstallBytes));
        checks.Add(RotationalCheck(installDir));

        checks.Add(await EngineCheckAsync(ct));

        return checks;
    }

    private static PreflightCheck DiskCheck(string name, string dir, long required)
    {
        try
        {
            var probe = FindExistingAncestor(dir);
            var free = new DriveInfo(probe).AvailableFreeSpace;
            var freeGb = free / (1024.0 * 1024 * 1024);
            var requiredGb = required / (1024.0 * 1024 * 1024);
            return free >= required
                ? new PreflightCheck(name, CheckState.Ok, $"{freeGb:F0} GB free")
                : new PreflightCheck(
                    name,
                    CheckState.Fail,
                    $"{freeGb:F0} GB free, {requiredGb:F0} GB needed at {dir}"
                );
        }
        catch (Exception e)
        {
            return new PreflightCheck(name, CheckState.Warn, $"Could not check {dir}: {e.Message}");
        }
    }

    /// <summary>Warn when the install dir sits on a rotational disk — LoreRim wants an SSD.</summary>
    private static PreflightCheck RotationalCheck(string installDir)
    {
        try
        {
            var probe = FindExistingAncestor(installDir);
            var device = FindBlockDevice(probe);
            if (device is null)
            {
                return new PreflightCheck("Install disk type", CheckState.Ok, "unknown device — skipped");
            }
            var rotationalPath = $"/sys/block/{device}/queue/rotational";
            if (File.Exists(rotationalPath) && File.ReadAllText(rotationalPath).Trim() == "1")
            {
                return new PreflightCheck(
                    "Install disk type",
                    CheckState.Warn,
                    "Install directory is on a spinning disk; LoreRim strongly recommends an SSD."
                );
            }
            return new PreflightCheck("Install disk type", CheckState.Ok, "SSD/NVMe");
        }
        catch (Exception)
        {
            return new PreflightCheck("Install disk type", CheckState.Ok, "could not determine — skipped");
        }
    }

    private async Task<PreflightCheck> EngineCheckAsync(CancellationToken ct)
    {
        if (engineLocator.EnginePath is null)
        {
            return new PreflightCheck(
                "Install engine",
                CheckState.Fail,
                "jackify-engine not bundled. Run scripts/setup-deps.sh (dev) or reinstall the AppImage."
            );
        }
        try
        {
            var (code, output) = await engineRunner.RunCaptureAsync(
                ["--version"],
                ct,
                TimeSpan.FromSeconds(30)
            );
            return code == 0
                ? new PreflightCheck("Install engine", CheckState.Ok, output.Trim())
                : new PreflightCheck("Install engine", CheckState.Fail, $"--version exited with {code}");
        }
        catch (Exception e)
        {
            return new PreflightCheck("Install engine", CheckState.Fail, e.Message);
        }
    }

    private static string FindExistingAncestor(string dir)
    {
        var current = Path.GetFullPath(dir);
        while (!Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current) ?? "/";
        }
        return current;
    }

    /// <summary>Best-effort parent block device (e.g. nvme0n1) for the filesystem holding path.</summary>
    private static string? FindBlockDevice(string path)
    {
        // Resolve the mount's source device via /proc/mounts (longest matching mountpoint).
        string? device = null;
        var bestLen = -1;
        foreach (var line in File.ReadAllLines("/proc/mounts"))
        {
            var parts = line.Split(' ');
            if (parts.Length < 2 || !parts[0].StartsWith("/dev/"))
            {
                continue;
            }
            var mountPoint = parts[1];
            if (path.StartsWith(mountPoint, StringComparison.Ordinal) && mountPoint.Length > bestLen)
            {
                bestLen = mountPoint.Length;
                device = parts[0];
            }
        }
        if (device is null)
        {
            return null;
        }
        var name = Path.GetFileName(device);
        // Strip partition suffix: sda1 → sda, nvme0n1p2 → nvme0n1.
        foreach (var block in Directory.GetDirectories("/sys/block"))
        {
            var blockName = Path.GetFileName(block);
            if (name.StartsWith(blockName, StringComparison.Ordinal))
            {
                return blockName;
            }
        }
        return null;
    }
}
