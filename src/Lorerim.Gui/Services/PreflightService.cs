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

        // These paths end up inside quoted Steam launch options and VDF strings, where
        // a double-quote, colon or newline corrupts the command Steam later runs.
        if ((PathProblem(installDir) ?? PathProblem(downloadDir)) is { } pathProblem)
        {
            checks.Add(new PreflightCheck("Folder names", CheckState.Fail, pathProblem));
        }

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

        checks.Add(ProtonCheck(compatTools.Scan(steam?.Root)));

        checks.AddRange(
            SpaceChecks(installDir, downloadDir, RequiredDownloadBytes, RequiredInstallBytes)
        );
        checks.Add(RotationalCheck(installDir));

        checks.Add(await EngineCheckAsync(ct));

        return checks;
    }

    /// <summary>
    /// LoreRim pins GE-Proton10-34 for ENB; anything else is reported with the reason so the
    /// user can install the right build before the Steam shortcut is created.
    /// </summary>
    private static PreflightCheck ProtonCheck(List<CompatTool> tools)
    {
        if (tools.Count == 0)
        {
            return new PreflightCheck("Proton", CheckState.Fail, LorerimProton.Guidance);
        }
        var (suitability, best) = LorerimProton.Best(tools);
        return suitability switch
        {
            ProtonSuitability.Required => new PreflightCheck(
                "Proton",
                CheckState.Ok,
                $"{best!.DisplayName} — {LorerimProton.Describe(suitability)}"
            ),
            ProtonSuitability.Compatible => new PreflightCheck(
                "Proton",
                CheckState.Warn,
                $"{best!.DisplayName} — {LorerimProton.Describe(suitability)}. {LorerimProton.Guidance}"
            ),
            _ => new PreflightCheck(
                "Proton",
                CheckState.Warn,
                $"Best available is {best!.DisplayName} — {LorerimProton.Describe(suitability)}. "
                    + LorerimProton.Guidance
            ),
        };
    }

    /// <summary>
    /// Space checks. When both folders live on the same filesystem their requirements add up,
    /// so they must be checked together — two independent checks can each pass while the
    /// install still runs the disk dry partway through.
    /// </summary>
    internal static List<PreflightCheck> SpaceChecks(
        string installDir,
        string downloadDir,
        long requiredDownload,
        long requiredInstall
    )
    {
        try
        {
            var installVolume = VolumeFor(installDir);
            var downloadVolume = VolumeFor(downloadDir);
            if (installVolume is null || downloadVolume is null)
            {
                return
                [
                    new PreflightCheck(
                        "Disk space",
                        CheckState.Warn,
                        "Could not determine which drive these folders are on."
                    ),
                ];
            }
            if (
                string.Equals(
                    installVolume.RootDirectory.FullName,
                    downloadVolume.RootDirectory.FullName,
                    StringComparison.Ordinal
                )
            )
            {
                return
                [
                    BuildSpaceCheck(
                        "Disk space",
                        installVolume.AvailableFreeSpace,
                        requiredDownload + requiredInstall,
                        $"{installVolume.RootDirectory.FullName} (downloads and install share it)",
                        // One budget covers both folders here, so either one already being on
                        // disk means the requirement overstates what the run actually needs.
                        HasExistingInstall(installDir) || HasExistingDownloads(downloadDir)
                    ),
                ];
            }
            return
            [
                BuildSpaceCheck(
                    "Download space",
                    downloadVolume.AvailableFreeSpace,
                    requiredDownload,
                    downloadVolume.RootDirectory.FullName,
                    HasExistingDownloads(downloadDir)
                ),
                BuildSpaceCheck(
                    "Install space",
                    installVolume.AvailableFreeSpace,
                    requiredInstall,
                    installVolume.RootDirectory.FullName,
                    HasExistingInstall(installDir)
                ),
            ];
        }
        catch (Exception e)
        {
            return [new PreflightCheck("Disk space", CheckState.Warn, $"Could not check: {e.Message}")];
        }
    }

    /// <summary>
    /// The mounted filesystem holding <paramref name="path"/>. DriveInfo(path) just echoes the
    /// path back on Unix, so identify the volume by longest mount-point prefix instead —
    /// otherwise two folders on one disk look like two independent budgets.
    /// </summary>
    private static DriveInfo? VolumeFor(string path)
    {
        var full = Path.GetFullPath(path);
        DriveInfo? best = null;
        var bestLength = -1;
        foreach (var drive in DriveInfo.GetDrives())
        {
            string root;
            try
            {
                root = drive.RootDirectory.FullName;
                _ = drive.AvailableFreeSpace; // skip pseudo/unreadable filesystems
            }
            catch (Exception)
            {
                continue;
            }
            if (!IsUnder(full, root) || root.Length <= bestLength)
            {
                continue;
            }
            best = drive;
            bestLength = root.Length;
        }
        return best;
    }

    /// <summary>Reason a folder path can't be used safely in launch options/VDF, or null if fine.</summary>
    internal static string? PathProblem(string dir)
    {
        foreach (var c in dir)
        {
            if (c == '"' || c == ':' || char.IsControl(c))
            {
                var shown = c == '"' ? "a double quote" : c == ':' ? "a colon" : "a control character";
                return $"'{dir}' contains {shown} — Steam launch options can't represent that; pick a simpler folder name.";
            }
        }
        return null;
    }

    /// <summary>Prefix match on directory boundaries, so /mnt/Games2 isn't "under" /mnt/Games.</summary>
    internal static bool IsUnder(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.Ordinal))
        {
            return true;
        }
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Headroom an update still needs even though most of the modlist is already on disk.
    /// Below this, a re-run genuinely cannot write and should stop.
    /// </summary>
    public const long UpdateHeadroomBytes = 20L * 1024 * 1024 * 1024;

    /// <summary>
    /// Whether a modlist is already installed here. Deliberately two cheap lookups rather
    /// than a walk: preflight runs every time the page opens, and the install tree holds
    /// hundreds of thousands of files.
    /// </summary>
    internal static bool HasExistingInstall(string installDir)
    {
        try
        {
            return File.Exists(Path.Join(installDir, "ModOrganizer.exe"))
                // Mid-run the engine deletes and rewrites files at the root, so a populated
                // mods tree is the more dependable signal.
                || Directory.EnumerateFileSystemEntries(Path.Join(installDir, "mods")).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool HasExistingDownloads(string downloadDir)
    {
        try
        {
            return Directory.EnumerateFiles(downloadDir).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static PreflightCheck BuildSpaceCheck(
        string name,
        long free,
        long required,
        string where,
        bool existingInstall = false
    )
    {
        var freeGb = free / (1024.0 * 1024 * 1024);
        var requiredGb = required / (1024.0 * 1024 * 1024);
        if (free >= required)
        {
            return new PreflightCheck(name, CheckState.Ok, $"{freeGb:F0} GB free on {where}");
        }
        // The requirement describes a fresh install. Re-running over an existing one reuses
        // what is already there, so demanding room for a second copy would block the update
        // and push the user into deleting the install they are trying to update.
        if (existingInstall && free >= UpdateHeadroomBytes)
        {
            return new PreflightCheck(
                name,
                CheckState.Warn,
                $"{freeGb:F0} GB free on {where}, below the {requiredGb:F0} GB a fresh install "
                    + "needs — continuing anyway because an existing install is already there and "
                    + "the engine only downloads what changed"
            );
        }
        return new PreflightCheck(
            name,
            CheckState.Fail,
            $"{freeGb:F0} GB free on {where}, {requiredGb:F0} GB needed — "
                + "point one of the folders at another drive, or free up space"
        );
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
            if (IsUnder(path, mountPoint) && mountPoint.Length > bestLen)
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
