using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Lorerim.Gui.Services.Engine;

namespace Lorerim.Gui.Services.Modlist;

/// <summary>
/// Binary fixes applied to a finished install. The engine faithfully installs what the
/// modlist specifies, which on Linux includes DLLs that only work on Windows; swapping them
/// is the last step between "installed" and "launches".
/// </summary>
public class ModFixupService(
    IHttpClientFactory hcf,
    JackifyEngineLocator engineLocator,
    LogService log
)
{
    /// <summary>Every known fixup, in one pass. A fully patched install is a cheap no-op.</summary>
    public async Task ApplyAsync(string installDir, CancellationToken ct = default)
    {
        await ApplyJContainersFixAsync(installDir, ct);
    }

    /// <summary>
    /// Replaces every crash-prone JContainers DLL with the patched build. Returns how many
    /// were replaced; 0 means there was nothing to do, which is the common case on re-runs.
    /// Each outcome is logged distinctly — "I checked and nothing needed patching" and "I
    /// could not tell" look identical to a user staring at a crash, so they must not read
    /// the same in the log.
    /// </summary>
    public async Task<int> ApplyJContainersFixAsync(
        string installDir,
        CancellationToken ct = default
    )
    {
        if (JContainersFix.FindAll(installDir).Count == 0)
        {
            log.Append("JContainers fix: this modlist does not ship JContainers, nothing to do");
            return 0;
        }
        if (!JContainersFix.TargetsSupportedSkse(installDir))
        {
            log.Append(
                $"JContainers fix: skipped — the patched build targets {JContainersFix.TargetSkseDll} "
                    + "and this install does not use it. If LoreRim crashes on launch, this is the first "
                    + "thing to check."
            );
            return 0;
        }
        var outdated = JContainersFix.FindOutdated(installDir);
        if (outdated.Count == 0)
        {
            log.Append("JContainers fix: already the Linux-compatible build");
            return 0;
        }
        // Refusing loudly beats skipping quietly: without this fix the modlist crashes on
        // launch with nothing to point at, which is exactly the failure this step exists for.
        var extractor =
            JContainersFix.ExtractorPath(engineLocator.EngineDir)
            ?? throw new InvalidOperationException(
                $"{outdated.Count} JContainers DLL(s) need the Linux-compatible build, but the "
                    + "bundled extractor is missing. The modlist is installed and will crash on "
                    + "launch until this runs; re-run Steam setup to retry."
            );

        log.Append($"JContainers fix: {outdated.Count} DLL(s) need the Linux-compatible build");
        var staging = Directory.CreateTempSubdirectory("lorerim-jcontainers");
        try
        {
            var archive = Path.Join(staging.FullName, "jcontainers.7z");
            await DownloadVerifiedAsync(archive, MaxArchiveBytes, ct);

            // "--" ends switch parsing, so an archive entry beginning with "-" is treated as
            // a name rather than a 7-Zip flag.
            var listing = await RunExtractorAsync(extractor, ct, "l", "-ba", "-slt", "--", archive);
            var entry =
                JContainersFix.DllPathInArchive(listing)
                ?? throw new InvalidOperationException(
                    $"No usable {JContainersFix.DllName} entry inside the downloaded archive"
                );
            await RunExtractorAsync(
                extractor,
                ct,
                "e",
                $"-o{staging.FullName}",
                "-y",
                "--",
                archive,
                entry
            );

            var patched = Path.Join(staging.FullName, JContainersFix.DllName);
            if (!File.Exists(patched))
            {
                throw new InvalidOperationException(
                    $"{JContainersFix.DllName} was not extracted from the downloaded archive"
                );
            }
            foreach (var target in outdated)
            {
                // Verifies the patched build's hash before it touches the file.
                JContainersFix.Replace(target, patched, JContainersFix.FixedSha256);
                log.Append($"JContainers fix: replaced {target}");
            }
            log.Append($"JContainers fix: patched {outdated.Count} DLL(s) for Linux");
            return outdated.Count;
        }
        finally
        {
            try
            {
                staging.Delete(recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover temp dir is not worth failing a finished install over.
            }
        }
    }

    /// <summary>
    /// The published archive is around 1 MB; the ceiling is generous enough to survive a
    /// re-release but keeps a broken or hostile server from filling the temp filesystem.
    /// </summary>
    private const long MaxArchiveBytes = 64L * 1024 * 1024;

    /// <summary>Generous enough for a slow link, finite so a stalled read cannot hang a run.</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ExtractorTimeout = TimeSpan.FromMinutes(2);
    private const int DownloadAttempts = 3;

    /// <summary>
    /// Downloads the archive and checks it against the pinned hash before anything parses it.
    /// A transient failure is retried: a 1 MB download must not be what fails a finished
    /// multi-hour install.
    /// </summary>
    internal async Task DownloadVerifiedAsync(string destination, long maxBytes, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var actual = await DownloadAsync(destination, maxBytes, ct);
                if (!actual.Equals(JContainersFix.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"The JContainers archive failed checksum verification (expected "
                            + $"{JContainersFix.ArchiveSha256}, got {actual}). Refusing to extract it."
                    );
                }
                return;
            }
            catch (Exception e)
                when (attempt < DownloadAttempts
                    && e is HttpRequestException or IOException or TimeoutException
                )
            {
                log.Append($"JContainers fix: download attempt {attempt} failed ({e.Message}); retrying");
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
    }

    /// <summary>Streams the archive to disk and returns its SHA-256, hashing what it writes.</summary>
    internal async Task<string> DownloadAsync(
        string destination,
        long maxBytes,
        CancellationToken ct
    )
    {
        var http = hcf.CreateClient("modFixup");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DownloadTimeout);
        try
        {
            using var response = await http.GetAsync(
                JContainersFix.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token
            );
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maxBytes)
            {
                throw new InvalidOperationException(
                    $"The JContainers archive claims {response.Content.Headers.ContentLength} bytes, "
                        + $"more than the {maxBytes} byte ceiling; refusing to download it."
                );
            }
            await using var source = await response.Content.ReadAsStreamAsync(cts.Token);
            await using var file = File.Create(destination);
            using var sha = SHA256.Create();
            var buffer = new byte[1 << 16];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cts.Token)) > 0)
            {
                written += read;
                if (written > maxBytes)
                {
                    throw new InvalidOperationException(
                        $"Download of the JContainers archive exceeded {maxBytes} bytes; abandoning "
                            + "it rather than filling the disk."
                    );
                }
                await file.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
            sha.TransformFinalBlock([], 0, 0);
            return Convert.ToHexStringLower(sha.Hash!);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient surfaces its own deadline as a cancellation, which the operation
            // runner would otherwise report as "cancelled" — as if the user had pressed Cancel.
            throw new TimeoutException(
                $"Downloading the JContainers archive timed out after {DownloadTimeout}."
            );
        }
    }

    /// <summary>Runs the bundled 7-Zip and returns stdout. Arguments are passed unparsed.</summary>
    private static async Task<string> RunExtractorAsync(
        string extractor,
        CancellationToken ct,
        params string[] args
    )
    {
        var psi = new ProcessStartInfo(extractor)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        using var process =
            Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {extractor}");
        // Both pipes must be drained concurrently: draining one to the end while the other
        // fills its buffer deadlocks the child.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ExtractorTimeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Otherwise a cancelled or stalled run leaves 7-Zip writing into the staging
            // directory we are about to delete.
            Kill(process);
            if (ct.IsCancellationRequested)
            {
                throw;
            }
            throw new TimeoutException(
                $"{Path.GetFileName(extractor)} timed out after {ExtractorTimeout}."
            );
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        // 7-Zip returns 1 for warnings it recovered from. The extracted DLL is hash-checked
        // before it is used, so a warning is not worth failing a finished install over.
        if (process.ExitCode > 1)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(extractor)} {string.Join(' ', args)} failed "
                    + $"(exit {process.ExitCode}): {stderr.Trim()}"
            );
        }
        return stdout;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already exited, or we cannot signal it; the cancellation still stands.
        }
    }
}
