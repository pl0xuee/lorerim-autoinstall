using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Lorerim.Gui.Services.Engine;

/// <summary>
/// One structured error the engine reported, with the sentence a user should read for it.
/// <see cref="Explanation"/> is a single line: it leads the status bar and the log pane alike.
/// </summary>
public sealed record EngineStructuredError(string Type, string Message, string Explanation);

/// <summary>
/// Reads the engine's structured error channel. jackify-engine writes failures to stderr as
/// one JSON object per line — <c>{"je":"1","level":"error","type":"disk_full","message":…,
/// "context":{…}}</c> — and exits with a code that names the class of failure (Jackify's
/// engine_error_parser.py consumes the same contract). Without this every failure read as
/// "exited with code 6", after hours, with the cause only in a raw JSON line in the log.
/// </summary>
public static class EngineErrorParser
{
    /// <summary>Returns null for anything that is not an error-level structured line.</summary>
    public static EngineStructuredError? TryParse(string line)
    {
        var trimmed = line.Trim();
        // Cheap rejection first: this runs on every line of a multi-hour install's output.
        if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed.Length > 100_000)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasMarker(root))
            {
                return null;
            }
            if (string.Equals(String(root, "level"), "warning", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            var type = String(root, "type") ?? "engine_error";
            var message = (String(root, "message") ?? "").Trim();
            // A missing context leaves the default (undefined) element, which Objects() treats
            // as "no entries" rather than throwing.
            root.TryGetProperty("context", out var context);
            var explanation =
                type == "missing_archives"
                    ? ExplainMissingArchives(message, context)
                    : Explain(type, message);
            return new EngineStructuredError(type, message, explanation);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What an exit code alone says, for a run that died without a structured line. Null means
    /// the code names nothing specific, and the caller falls back to its generic message.
    /// </summary>
    public static string? ForExitCode(int exitCode) =>
        exitCode switch
        {
            2 => Explain("auth_failed", ""),
            3 => Explain("network_error", ""),
            4 => Explain("disk_full", ""),
            5 => Explain("validation_failed", ""),
            _ => null,
        };

    // The marker is the string "1" in practice; a bare number is accepted rather than have a
    // serialiser change turn every structured error back into a raw log line.
    private static bool HasMarker(JsonElement root) =>
        root.TryGetProperty("je", out var je)
        && (
            (je.ValueKind == JsonValueKind.String && je.GetString() == "1")
            || (je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var n) && n == 1)
        );

    private static readonly Dictionary<string, (string Title, string Hint)> Types = new()
    {
        ["auth_failed"] = (
            "Nexus sign-in failed",
            "Sign out and back in on the Install page, or re-validate your API key in Settings, then retry."
        ),
        ["premium_required"] = (
            "Nexus Premium required",
            "Sign in with a Premium account — a free account has to download each file by hand."
        ),
        ["network_error"] = (
            "Network error",
            "The install resumes where it stopped, so re-run it once the connection is back."
        ),
        ["disk_full"] = (
            "Disk full",
            "Free space on the install or downloads drive, then re-run — it resumes where it stopped."
        ),
        ["disk_error"] = ("Disk error", "If it repeats, check the drive's health."),
        ["permission_denied"] = (
            "Permission denied",
            "Choose install and download folders you own."
        ),
        ["archive_corrupt"] = (
            "Corrupt download",
            "Re-run the install; the bad file is downloaded again."
        ),
        ["file_not_found"] = (
            "File not found",
            "Check that the folders exist and Skyrim is installed, then re-run."
        ),
        ["download_stalled"] = (
            "Downloads stalled",
            "Check your connection and Nexus sign-in, then re-run."
        ),
        ["validation_failed"] = (
            "Validation failed",
            "Re-run the install to download whatever failed again."
        ),
        ["engine_error"] = ("Install engine error", ""),
    };

    private static string Explain(string type, string message)
    {
        var (title, hint) = Types.TryGetValue(type, out var known) ? known : (type, "");
        var body = message.Length > 0 ? $"{title}: {message}" : $"{title}.";
        // The engine's own sentence often ends in a full stop already; don't double it.
        return hint.Length > 0 ? $"{body.TrimEnd()} {hint}" : body;
    }

    /// <summary>
    /// A missing archive is one of two very different things, and the engine reports both under
    /// one type: a real download that failed, or one of the user's own game files not matching
    /// what the modlist was built against — usually Steam having updated Skyrim.
    /// </summary>
    private static string ExplainMissingArchives(string message, JsonElement context)
    {
        var mismatches = Objects(context, "game_file_mismatches");
        var failures = Objects(context, "download_failures");
        var parts = new List<string>();

        if (mismatches.Count > 0)
        {
            var total =
                context.TryGetProperty("game_file_total", out var t) && t.TryGetInt32(out var n) && n > 0
                    ? n
                    : mismatches.Count;
            var versions = mismatches
                .Select(m => String(m, "game_version")?.Trim())
                .Where(v => !string.IsNullOrEmpty(v))
                .Distinct()
                .ToList();
            var expects = versions.Count == 1 ? $" (it expects game version {versions[0]})" : "";
            parts.Add(
                mismatches.Count == 1
                    ? $"'{String(mismatches[0], "name") ?? "a required file"}' comes from your own "
                        + $"Skyrim install, not a download, and doesn't match what LoreRim expects{expects}."
                    : $"{mismatches.Count} of {total} game files don't match what LoreRim expects{expects}."
            );
            parts.Add(
                "Steam has most likely updated Skyrim past the version this modlist was built for, "
                    + "or the game files are damaged: verify them in Steam, and check the LoreRim "
                    + "page for the game version it supports."
            );
        }

        if (failures.Count > 0)
        {
            var names = string.Join(", ", failures.Take(5).Select(f => String(f, "name") ?? "unknown"));
            if (failures.Count > 5)
            {
                names += $", and {failures.Count - 5} more";
            }
            parts.Add($"{failures.Count} file(s) failed to download: {names}. Re-run the install; it retries them.");
        }

        return parts.Count > 0
            ? string.Join(' ', parts)
            : message.Length > 0
                ? message
                : "One or more archives could not be located.";
    }

    private static List<JsonElement> Objects(JsonElement context, string name) =>
        context.ValueKind == JsonValueKind.Object
        && context.TryGetProperty(name, out var array)
        && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)]
            : [];

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
