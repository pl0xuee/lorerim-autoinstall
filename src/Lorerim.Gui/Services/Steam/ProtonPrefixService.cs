using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Lorerim.Gui.Services.Steam;

/// <summary>
/// Creates the Proton prefix for a shortcut without launching anything through Steam
/// (Jackify automated_prefix_creation port): `proton run wineboot -u` with
/// STEAM_COMPAT_DATA_PATH, DISPLAY blanked so it runs invisibly.
/// </summary>
public class ProtonPrefixService(LogService log)
{
    public async Task CreateAsync(
        SteamInstallation steam,
        CompatTool tool,
        long unsignedAppId,
        CancellationToken ct = default
    )
    {
        var compatData = Path.Join(steam.CompatDataDir, unsignedAppId.ToString());
        Directory.CreateDirectory(compatData);
        var pfx = Path.Join(compatData, "pfx");
        if (Directory.Exists(pfx) && File.Exists(Path.Join(pfx, "system.reg")))
        {
            log.Append($"Prefix already exists at {pfx}");
            PrepareGameAppData(pfx);
            return;
        }

        log.Append($"Creating Proton prefix with {tool.DisplayName}…");
        var psi = new ProcessStartInfo
        {
            FileName = tool.ProtonBinary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("wineboot");
        psi.ArgumentList.Add("-u");
        psi.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = steam.Root;
        psi.Environment["STEAM_COMPAT_DATA_PATH"] = compatData;
        psi.Environment["WINEDEBUG"] = "-all";
        psi.Environment["DISPLAY"] = "";
        psi.Environment["WAYLAND_DISPLAY"] = "";

        using var p = Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEndAsync(ct);
        _ = p.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(180));
        try
        {
            await p.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(true);
            }
            catch
            {
                // already gone
            }
            if (ct.IsCancellationRequested)
            {
                throw;
            }
            log.Append("wineboot timed out; polling for prefix…");
        }

        // Success criterion is the prefix existing, not wineboot's exit code.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(pfx) && File.Exists(Path.Join(pfx, "system.reg")))
            {
                log.Append($"Prefix created at {pfx}");
                PrepareGameAppData(pfx);
                return;
            }
            await Task.Delay(1000, ct);
        }
        throw new InvalidOperationException($"Proton prefix was not created at {pfx}");
    }

    /// <summary>
    /// Runs on both paths out of <see cref="CreateAsync"/>: a prefix that already exists needs
    /// this just as much as a new one, since the folder is missing until Skyrim itself makes it.
    /// </summary>
    private void PrepareGameAppData(string pfx)
    {
        var created = EnsureSkyrimLocalAppData(pfx);
        if (created > 0)
        {
            log.Append(
                $"Prepared the game's AppData folder in the prefix ({created}) so Mod Organizer "
                    + "can apply the load order."
            );
        }
    }

    /// <summary>
    /// Creates AppData/Local/Skyrim Special Edition inside the prefix, for every user profile
    /// it contains.
    ///
    /// MO2 redirects the game's plugins.txt onto that folder. On Windows it always exists
    /// because Skyrim has run; in a shortcut's freshly created Proton prefix it does not,
    /// because Skyrim runs under its own appid in a different prefix. Without it MO2
    /// establishes no redirect, Skyrim writes its own load order with every plugin disabled,
    /// and the game launches as though no mod were installed — surfacing as a fatal error
    /// from whichever mod checks for its plugin first, which blames itself rather than this.
    ///
    /// Returns how many were created. Nothing is written when they already exist: the folder
    /// holds the load order once the game has run, and it must never be disturbed.
    /// </summary>
    internal static int EnsureSkyrimLocalAppData(string pfx)
    {
        var users = Path.Join(pfx, "drive_c", "users");
        if (!Directory.Exists(users))
        {
            return 0;
        }
        var created = 0;
        foreach (var user in Directory.GetDirectories(users))
        {
            // "Public" is a shared skeleton profile; the game never reads a load order there.
            if (Path.GetFileName(user).Equals("Public", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var gameDir = Path.Join(user, "AppData", "Local", "Skyrim Special Edition");
            if (Directory.Exists(gameDir))
            {
                continue;
            }
            try
            {
                Directory.CreateDirectory(gameDir);
                created++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a prefix we cannot write to is a problem the caller will hit
                // more loudly than this, and throwing here would fail an otherwise fine setup.
            }
        }
        return created;
    }

    public bool PrefixExists(SteamInstallation steam, long unsignedAppId) =>
        File.Exists(Path.Join(steam.CompatDataDir, unsignedAppId.ToString(), "pfx", "system.reg"));
}
