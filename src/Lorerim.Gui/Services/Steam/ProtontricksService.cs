using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Lorerim.Gui.Services.Steam;

/// <summary>
/// Runs protontricks to install prefix prerequisites (Jackify protontricks_prefix port).
/// Skyrim/MO2 verb set comes from Jackify's modlist_wine_ops defaults + Skyrim extras;
/// dxvk is omitted because Proton ships its own.
/// </summary>
public class ProtontricksService(LogService log)
{
    public static readonly string[] Components =
    [
        "fontsmooth=rgb",
        "xact",
        "xact_x64",
        "vcrun2022",
        "d3dcompiler_47",
        "d3dx11_43",
        "d3dcompiler_43",
        "dotnet6",
        "dotnet7",
        "dotnet8",
        "dotnet9",
        "dotnetdesktop6",
        "dotnetdesktop9",
    ];

    /// <summary>Verbs that show up in `list-installed` (settings verbs like fontsmooth don't).</summary>
    private static readonly string[] VerifiableComponents =
        [.. Components.Where(c => !c.Contains('='))];

    public async Task<(bool Ok, string Version)> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var (code, stdout, _) = await RunAsync(
                "protontricks",
                ["-V"],
                TimeSpan.FromSeconds(20),
                ct
            );
            return code == 0 ? (true, stdout.Trim()) : (false, "");
        }
        catch
        {
            return (false, "");
        }
    }

    public async Task InstallComponentsAsync(
        long unsignedAppId,
        string? prefixPath = null,
        CancellationToken ct = default
    )
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            log.Append(
                $"protontricks attempt {attempt}/3: installing {string.Join(" ", Components)}"
            );
            // dotnet6–9 are slow winetricks verbs; give the batch generous headroom.
            var (code, tail) = await RunStreamingAsync(
                "protontricks",
                ["--no-bwrap", unsignedAppId.ToString(), "-q", .. Components],
                TimeSpan.FromMinutes(45),
                ct
            );
            if (code != 0 && !string.IsNullOrWhiteSpace(tail))
            {
                log.Append(tail);
            }
            if (code == 0 || await VerifyAsync(unsignedAppId, ct))
            {
                log.Append("Components installed");
                await SetWin10Async(unsignedAppId, ct);
                return;
            }
            log.Append($"protontricks exited with {code}; cleaning up wine processes before retry");
            Cleanup(prefixPath);
        }
        throw new InvalidOperationException("protontricks failed after 3 attempts — see log");
    }

    public async Task<bool> VerifyAsync(long unsignedAppId, CancellationToken ct = default)
    {
        var (code, stdout, _) = await RunAsync(
            "protontricks",
            ["--no-bwrap", unsignedAppId.ToString(), "list-installed"],
            TimeSpan.FromMinutes(2),
            ct
        );
        if (code != 0)
        {
            return false;
        }
        // vcrun2022 shows up as vcrun2022; the d3d/x components appear verbatim.
        return VerifiableComponents.All(c => stdout.Contains(c, StringComparison.OrdinalIgnoreCase));
    }

    private async Task SetWin10Async(long unsignedAppId, CancellationToken ct)
    {
        log.Append("Setting Windows 10 mode");
        var (code, _, stderr) = await RunAsync(
            "protontricks",
            ["--no-bwrap", unsignedAppId.ToString(), "win10"],
            TimeSpan.FromMinutes(3),
            ct
        );
        if (code != 0)
        {
            log.Append($"win10 mode failed (non-fatal): {Tail(stderr)}");
        }
    }

    private void Cleanup(string? prefixPath)
    {
        // wineserver -k with no WINEPREFIX targets ~/.wine — killing the user's unrelated
        // Wine apps while leaving the stuck GAMMA-prefix processes running. Scope the kill
        // to the actual prefix; if we don't know it, do nothing rather than nuke ~/.wine.
        if (string.IsNullOrEmpty(prefixPath))
        {
            return;
        }
        try
        {
            var psi = BuildPsi("wineserver", ["-k"]);
            psi.Environment["WINEPREFIX"] = prefixPath;
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
        }
        catch
        {
            // wineserver may not be on PATH; best-effort cleanup only
        }
    }

    /// <summary>
    /// Like RunAsync but forwards winetricks' interesting lines to the log pane as they
    /// happen, so a multi-minute verb install isn't a silent wait. Returns the exit code
    /// and the output tail for failure diagnostics.
    /// </summary>
    private async Task<(int Code, string Tail)> RunStreamingAsync(
        string file,
        string[] args,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        var psi = BuildPsi(file, args);
        using var p = Process.Start(psi)!;
        var recent = new System.Collections.Concurrent.ConcurrentQueue<string>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        async Task Pump(StreamReader reader)
        {
            // Read on the timeout-linked token so a lingering child that inherited the pipe
            // write end (wine daemonizes) can't keep the reader open past the deadline.
            while (await reader.ReadLineAsync(cts.Token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                recent.Enqueue(line);
                while (recent.Count > 8)
                {
                    recent.TryDequeue(out _);
                }
                if (IsProgressLine(line))
                {
                    log.Append($"  {line.Trim()}");
                }
            }
        }

        var pumps = Task.WhenAll(Pump(p.StandardOutput), Pump(p.StandardError));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            p.Kill(true);
            if (ct.IsCancellationRequested)
            {
                throw;
            }
            throw new TimeoutException($"{file} timed out after {timeout}");
        }
        // Give the pipes a moment to drain; never let a stuck reader hang the pipeline.
        try
        {
            await pumps.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Process already exited; a pipe still open means an inherited handle. Drop it.
        }
        return (p.ExitCode, string.Join('\n', recent));
    }

    /// <summary>Winetricks lines worth surfacing: per-verb execution, downloads, problems.</summary>
    private static bool IsProgressLine(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("Executing w_do_call", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Executing load_", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Downloading", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Extracting", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Setting", StringComparison.OrdinalIgnoreCase)
            || t.Contains("error", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("warning:", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int Code, string StdOut, string StdErr)> RunAsync(
        string file,
        string[] args,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        var psi = BuildPsi(file, args);
        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            p.Kill(true);
            if (ct.IsCancellationRequested)
            {
                throw;
            }
            throw new TimeoutException($"{file} timed out after {timeout}");
        }
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }

    private static ProcessStartInfo BuildPsi(string file, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        psi.Environment["WINEDEBUG"] = "-all";
        psi.Environment["WINETRICKS_SUPER_QUIET"] = "1";
        return psi;
    }

    private void LogTail(string stdout, string stderr)
    {
        var tail = Tail(stdout + "\n" + stderr);
        if (!string.IsNullOrWhiteSpace(tail))
        {
            log.Append(tail);
        }
    }

    private static string Tail(string s, int lines = 8) =>
        string.Join(
            '\n',
            s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .TakeLast(lines)
        );
}
