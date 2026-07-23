using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lorerim.Gui.Services.Engine;

namespace Lorerim.Gui.Services.Modlist;

public sealed record ModlistInfo(
    string MachineUrl,
    string Title,
    long? DownloadSizeBytes,
    long? InstallSizeBytes
);

/// <summary>
/// Resolves LoreRim's machine URL and sizes from the engine catalog:
/// `jackify-engine list-modlists -n LoreRim -show-machine-url -show-all-sizes` prints
/// "LoreRim - Skyrim Special Edition - 233.3 GB|304.2 GB|537.5 GB - LoreRim/LoreRim".
/// Falls back to the known machine URL if the catalog can't be fetched.
/// </summary>
public partial class ModlistResolverService(JackifyEngineRunner runner, LogService log)
{
    public const string LorerimMachineUrl = "LoreRim/LoreRim";

    public async Task<ModlistInfo> ResolveLorerimAsync(CancellationToken ct)
    {
        try
        {
            var (exit, output) = await runner.RunCaptureAsync(
                ["list-modlists", "-n", "LoreRim", "-show-machine-url", "-show-all-sizes"],
                ct,
                TimeSpan.FromMinutes(3)
            );
            if (exit == 0 && ParseEntry(output) is { } found)
            {
                log.Append(
                    $"Resolved modlist: {found.Title} ({found.MachineUrl}), "
                        + $"downloads {FormatGb(found.DownloadSizeBytes)}, install {FormatGb(found.InstallSizeBytes)}"
                );
                return found;
            }
            log.Append(
                exit == 0
                    ? "LoreRim not found in the engine catalog; using the known machine URL."
                    : $"list-modlists exited with {exit}; using the known machine URL."
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            log.Append($"Modlist catalog fetch failed ({e.Message}); using the known machine URL.");
        }
        return new ModlistInfo(LorerimMachineUrl, "LoreRim", null, null);
    }

    // "<title> - <game> - <dl> GB|<install> GB|<total> GB - <repo/name>"
    [GeneratedRegex(
        @"^(?<title>.+?)\s+-\s+.+?\s+-\s+(?<dl>[\d.]+)\s*GB\|(?<inst>[\d.]+)\s*GB\|[\d.]+\s*GB\s+-\s+(?<url>\S+/\S+)\s*$",
        RegexOptions.Multiline
    )]
    private static partial Regex EntryRx();

    internal static ModlistInfo? ParseEntry(string output)
    {
        foreach (Match m in EntryRx().Matches(output))
        {
            if (!m.Groups["url"].Value.Contains("lorerim", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return new ModlistInfo(
                m.Groups["url"].Value,
                m.Groups["title"].Value.Trim(),
                GbToBytes(m.Groups["dl"].Value),
                GbToBytes(m.Groups["inst"].Value)
            );
        }
        return null;
    }

    private static long? GbToBytes(string gb) =>
        double.TryParse(gb, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? (long)(v * 1024 * 1024 * 1024)
            : null;

    private static string FormatGb(long? bytes) =>
        bytes is { } b ? $"{b / (1024.0 * 1024 * 1024):F1} GB" : "unknown size";
}
