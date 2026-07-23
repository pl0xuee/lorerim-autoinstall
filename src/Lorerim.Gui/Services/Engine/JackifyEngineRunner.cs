using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Lorerim.Gui.Services.Engine;

public sealed record EngineInstallRequest(
    string? WabbajackFile,
    string? MachineUrl,
    string InstallDir,
    string DownloadDir,
    IReadOnlyDictionary<string, string> AuthEnv
);

public sealed class EngineException(string message) : Exception(message);

/// <summary>
/// Drives the bundled jackify-engine subprocess. Invocation contract mirrors Jackify's
/// modlist_service_installation.py: cwd = engine dir, cleaned environment, stdout parsed
/// line-by-line (splitting on both \n and \r), JSON event lines answered over stdin.
/// </summary>
public partial class JackifyEngineRunner(
    JackifyEngineLocator locator,
    EngineProgress progress,
    LogService log
)
{
    /// <summary>Runs the engine and captures combined output — for --version, --help, list-modlists.</summary>
    public async Task<(int ExitCode, string Output)> RunCaptureAsync(
        IEnumerable<string> args,
        CancellationToken ct,
        TimeSpan? timeout = null
    )
    {
        var psi = BuildStartInfo(args, new Dictionary<string, string>());
        using var p = new Process();
        p.StartInfo = psi;
        var output = new System.Text.StringBuilder();
        void Collect(object _, DataReceivedEventArgs e)
        {
            if (e.Data is not null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
        }
        p.OutputDataReceived += Collect;
        p.ErrorDataReceived += Collect;
        if (!p.Start())
        {
            throw new EngineException("Failed to start jackify-engine");
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.StandardInput.Close();
        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(5);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(effectiveTimeout);
        try
        {
            await p.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch
            {
                // already exited
            }
            if (ct.IsCancellationRequested)
            {
                throw;
            }
            // Internal deadline, not the caller cancelling — reporting it as OCE would make
            // OperationRunner label a hung engine "cancelled" with no error surfaced.
            throw new TimeoutException($"jackify-engine timed out after {effectiveTimeout}");
        }
        // Sync WaitForExit (unlike the awaited one) drains the async output events, so the
        // final line of --version/list-modlists output isn't occasionally lost.
        p.WaitForExit();
        lock (output)
        {
            return (p.ExitCode, output.ToString());
        }
    }

    public async Task InstallAsync(EngineInstallRequest req, CancellationToken ct)
    {
        // Note: this engine version (0.5.7) has no --show-file-progress flag; progress
        // arrives as plain console lines and [FILE_PROGRESS] lines when supported.
        var args = new List<string> { "install" };
        if (req.WabbajackFile is not null)
        {
            args.AddRange(["-w", req.WabbajackFile]);
        }
        else if (req.MachineUrl is not null)
        {
            args.AddRange(["-m", req.MachineUrl]);
        }
        else
        {
            throw new EngineException("Neither a .wabbajack file nor a machine URL was provided");
        }
        args.AddRange(["-o", req.InstallDir, "-d", req.DownloadDir]);

        RaiseFileDescriptorLimit();

        var psi = BuildStartInfo(args, req.AuthEnv);
        using var p = new Process();
        p.StartInfo = psi;
        log.Append($"jackify-engine: {string.Join(' ', args)}");
        if (!p.Start())
        {
            throw new EngineException("Failed to start jackify-engine");
        }

        var state = new InstallLineState();

        using var reg = ct.Register(() =>
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch
            {
                // already exited
            }
        });

        // Drain stderr concurrently to avoid pipe deadlock; error lines go through the same
        // handler so CC-content detection sees them too.
        var stderrTask = Task.Run(
            async () =>
            {
                var lines = new List<string>();
                await foreach (var line in ReadLinesAsync(p.StandardError, ct))
                {
                    lines.Add(line);
                }
                return lines;
            },
            CancellationToken.None
        );

        try
        {
            await foreach (var line in ReadLinesAsync(p.StandardOutput, ct))
            {
                HandleLine(line, p, state);
            }
            foreach (var line in await stderrTask)
            {
                HandleLine(line, p, state);
            }
            await p.WaitForExitAsync(ct);
        }
        finally
        {
            if (!p.HasExited)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // already exited
                }
            }
        }

        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
        {
            throw new EngineException(
                BuildFailureMessage(p.ExitCode, state.CcErrors, state.CreationKitError)
            );
        }
    }

    private sealed class InstallLineState
    {
        public List<string> CcErrors { get; } = [];
        public bool CreationKitError { get; set; }
        public List<ManualDownload> PendingManual { get; } = [];
    }

    private void HandleLine(string line, Process p, InstallLineState state)
    {
        if (line.Length == 0)
        {
            return;
        }

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            if (TryHandleJsonEvent(trimmed, p, state.PendingManual))
            {
                return;
            }
        }

        if (EngineProgressParser.TryParse(line) is { } fp)
        {
            progress.PublishFile(fp);
            return;
        }

        if (IsCcContentError(line))
        {
            state.CcErrors.Add(line.Trim());
        }
        if (IsCreationKitMissingError(line))
        {
            state.CreationKitError = true;
        }
        log.Append($"[engine] {line}");
    }

    private bool TryHandleJsonEvent(string line, Process p, List<ManualDownload> pending)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return false;
        }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("event", out var evt))
            {
                return false;
            }
            switch (evt.GetString())
            {
                case "manual_download_required":
                    pending.Add(
                        new ManualDownload(
                            GetString(doc.RootElement, "name") ?? "unknown",
                            GetString(doc.RootElement, "url"),
                            GetString(doc.RootElement, "reason")
                        )
                    );
                    return true;
                case "manual_download_list_complete":
                    log.Append(
                        $"Engine requests {pending.Count} manual download(s); waiting for user."
                    );
                    progress.PublishManualDownloads(pending.ToArray());
                    // The UI calls ContinueManualDownloads() once the user is done; store
                    // the process so the reply reaches the right stdin.
                    _awaitingContinue = p;
                    return true;
                case "manual_download_phase_complete":
                    pending.Clear();
                    _awaitingContinue = null;
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>Tell a waiting engine that manual downloads are done (files are in the download dir).</summary>
    public void ContinueManualDownloads()
    {
        var p = _awaitingContinue;
        if (p is null || p.HasExited)
        {
            return;
        }
        try
        {
            p.StandardInput.WriteLine("{\"command\":\"continue\"}");
            p.StandardInput.Flush();
        }
        catch (IOException)
        {
            // engine went away; the exit path reports the real error
        }
    }

    private ProcessStartInfo BuildStartInfo(
        IEnumerable<string> args,
        IReadOnlyDictionary<string, string> extraEnv
    )
    {
        var engineDir =
            locator.EngineDir
            ?? throw new EngineException(
                "jackify-engine not found. Run scripts/setup-deps.sh or set LORERIM_ENGINE_PATH."
            );
        var psi = new ProcessStartInfo
        {
            FileName = Path.Join(engineDir, JackifyEngineLocator.BinaryName),
            WorkingDirectory = engineDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        // AppImage runtime vars would leak our bundled libraries into the engine process.
        foreach (
            var v in (string[])["APPIMAGE", "APPDIR", "ARGV0", "OWD", "LD_LIBRARY_PATH"]
        )
        {
            psi.Environment.Remove(v);
        }
        psi.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
        foreach (var (k, v) in extraEnv)
        {
            psi.Environment[k] = v;
        }
        return psi;
    }

    /// <summary>
    /// Reads stdout treating both \n and bare \r (progress-bar redraws) as line breaks.
    /// stderr is drained concurrently into the log to avoid pipe deadlock.
    /// </summary>
    private async IAsyncEnumerable<string> ReadLinesAsync(
        StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        var buf = new char[4096];
        var current = new System.Text.StringBuilder();
        while (true)
        {
            int n;
            try
            {
                n = await reader.ReadAsync(buf.AsMemory(), ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            if (n == 0)
            {
                break;
            }
            for (var i = 0; i < n; i++)
            {
                var c = buf[i];
                if (c is '\n' or '\r')
                {
                    if (current.Length > 0)
                    {
                        yield return current.ToString();
                        current.Clear();
                    }
                }
                else if (c != '\0')
                {
                    current.Append(c);
                }
            }
        }
        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static string BuildFailureMessage(
        int exitCode,
        List<string> ccErrors,
        bool creationKitError
    )
    {
        if (ccErrors.Count > 0)
        {
            return "Skyrim Anniversary Edition content is missing. Launch Skyrim once from Steam, "
                + "open the Creations menu and choose \"Download All\", then retry.\n"
                + string.Join('\n', ccErrors[..Math.Min(5, ccErrors.Count)]);
        }
        if (creationKitError)
        {
            return "This modlist needs the Skyrim Creation Kit installed in Steam. "
                + "Install \"Skyrim Special Edition: Creation Kit\" and retry.";
        }
        return $"jackify-engine exited with code {exitCode} — see the log for details.";
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // Port of Jackify's cc_content_detector.py heuristics.
    [GeneratedRegex(@"cc[a-z]{2,8}\d{3,4}[-\w]*\.(?:bsa|esm|esl|esp|ba2)", RegexOptions.IgnoreCase)]
    private static partial Regex CcFileRegex();

    private static readonly string[] ErrorWords =
    [
        "missing",
        "required",
        "failed",
        "unable",
        "cannot",
        "error",
        "not found",
    ];

    private static readonly string[] CkIndicators =
    [
        "creationkit",
        "papyrus compiler",
        "scriptcompile",
        "lipgen",
        "assetwatcher",
        "havokbehaviorpostprocess",
        "skyrimreservedaddonindexes",
        "p4com64",
        "lex_ssce",
    ];

    internal static bool IsCcContentError(string line)
    {
        var normalized = line.Trim().ToLowerInvariant();
        if (!CcFileRegex().IsMatch(normalized))
        {
            return false;
        }
        foreach (var w in ErrorWords)
        {
            if (normalized.Contains(w))
            {
                return true;
            }
        }
        return false;
    }

    internal static bool IsCreationKitMissingError(string line)
    {
        var normalized = line.Trim().ToLowerInvariant();
        if (!normalized.Contains("gamefilesource"))
        {
            return false;
        }
        foreach (var ind in CkIndicators)
        {
            if (normalized.Contains(ind))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Wabbajack opens thousands of files concurrently; raise the soft NOFILE limit to the
    /// hard limit (Jackify's increase_file_descriptor_limit). .NET has no managed API.
    /// </summary>
    internal static void RaiseFileDescriptorLimit()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }
        try
        {
            if (getrlimit(RLIMIT_NOFILE, out var lim) == 0 && lim.rlim_cur < lim.rlim_max)
            {
                lim.rlim_cur = lim.rlim_max;
                _ = setrlimit(RLIMIT_NOFILE, ref lim);
            }
        }
        catch
        {
            // best effort; the engine still works with default limits for small lists
        }
    }

    private const int RLIMIT_NOFILE = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct RLimit
    {
        public ulong rlim_cur;
        public ulong rlim_max;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int getrlimit(int resource, out RLimit rlim);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int setrlimit(int resource, ref RLimit rlim);

    private volatile Process? _awaitingContinue;
}
