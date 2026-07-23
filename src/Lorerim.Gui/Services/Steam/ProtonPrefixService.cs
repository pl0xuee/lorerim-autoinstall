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
                return;
            }
            await Task.Delay(1000, ct);
        }
        throw new InvalidOperationException($"Proton prefix was not created at {pfx}");
    }

    public bool PrefixExists(SteamInstallation steam, long unsignedAppId) =>
        File.Exists(Path.Join(steam.CompatDataDir, unsignedAppId.ToString(), "pfx", "system.reg"));
}
