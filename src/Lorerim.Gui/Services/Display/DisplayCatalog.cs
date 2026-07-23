using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lorerim.Gui.Services.Display;

public sealed record DisplayMode(int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height}";
}

public sealed record DisplayOutput(
    string Connector,
    DisplayMode? Native,
    IReadOnlyList<DisplayMode> Modes,
    bool IsPrimary
);

/// <summary>One resolution the user can pick, and which displays offer it natively.</summary>
public sealed record ResolutionChoice(
    DisplayMode Mode,
    IReadOnlyList<string> Displays,
    bool IsPrimaryNative
);

/// <summary>
/// Enumerates the resolutions this machine can actually display.
///
/// Modes come from /sys/class/drm, which is session-agnostic — it works under Wayland, X11 or
/// neither, with no external binary — and reports every mode a panel *supports*. xrandr is
/// deliberately not used for this: it reports the current layout, so a 4K panel being driven
/// at 1440p appears as a 1440p panel and a rotated one appears portrait. It is consulted only
/// to learn which output is primary, which sysfs does not express.
/// </summary>
public partial class DisplayCatalog(string? sysfsRoot = null, Func<string?>? xrandrQuery = null)
{
    private const string DefaultSysfsRoot = "/sys/class/drm";

    private readonly string _sysfsRoot = sysfsRoot ?? DefaultSysfsRoot;
    private readonly Func<string?> _xrandrQuery = xrandrQuery ?? RunXrandr;

    /// <summary>True when no primary could be established and the largest output was assumed.</summary>
    public bool PrimaryIsGuess { get; private set; }

    public IReadOnlyList<DisplayOutput> Scan()
    {
        if (!Directory.Exists(_sysfsRoot))
        {
            PrimaryIsGuess = false;
            return [];
        }

        var found = new List<(string Connector, List<DisplayMode> Modes)>();
        foreach (var dir in Directory.GetDirectories(_sysfsRoot).Order(StringComparer.Ordinal))
        {
            if (!ReadLine(Path.Join(dir, "status")).Equals("connected", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var modes = ReadModes(Path.Join(dir, "modes"));
            if (modes.Count == 0)
            {
                // Writeback and virtual connectors report as connected with nothing to show.
                continue;
            }
            found.Add((ConnectorName(Path.GetFileName(dir)), modes));
        }

        var primary = PrimaryFromXrandr();
        PrimaryIsGuess = primary is null && found.Count > 0;
        primary ??= found
            .OrderByDescending(f => f.Modes[0].Width * (long)f.Modes[0].Height)
            .Select(f => f.Connector)
            .FirstOrDefault();

        return
        [
            .. found.Select(f => new DisplayOutput(
                f.Connector,
                f.Modes[0],
                f.Modes,
                f.Connector.Equals(primary, StringComparison.OrdinalIgnoreCase)
            )),
        ];
    }

    /// <summary>
    /// Every distinct resolution across all displays, largest first, with the primary's native
    /// mode leading so the common case is the default.
    /// </summary>
    public IReadOnlyList<ResolutionChoice> Choices()
    {
        var outputs = Scan();
        if (outputs.Count == 0)
        {
            return [];
        }
        var primaryNative = outputs.FirstOrDefault(o => o.IsPrimary)?.Native;

        return
        [
            .. outputs
                .SelectMany(o => o.Modes.Select(m => (Mode: m, o.Connector)))
                .GroupBy(x => x.Mode)
                .Select(g => new ResolutionChoice(
                    g.Key,
                    [.. g.Select(x => x.Connector).Distinct().Order(StringComparer.Ordinal)],
                    g.Key == primaryNative
                ))
                .OrderByDescending(c => c.IsPrimaryNative)
                .ThenByDescending(c => c.Mode.Width * (long)c.Mode.Height),
        ];
    }

    private string? PrimaryFromXrandr()
    {
        string? output;
        try
        {
            output = _xrandrQuery();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return null;
        }
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }
        var match = PrimaryRx().Match(output);
        return match.Success ? match.Groups["name"].Value : null;
    }

    /// <summary>Strips the card prefix: "card1-DP-1" is the connector "DP-1".</summary>
    private static string ConnectorName(string directoryName)
    {
        var dash = directoryName.IndexOf('-');
        return dash >= 0 ? directoryName[(dash + 1)..] : directoryName;
    }

    private static string ReadLine(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static List<DisplayMode> ReadModes(string path)
    {
        var modes = new List<DisplayMode>();
        var seen = new HashSet<DisplayMode>();
        foreach (var line in ReadLine(path).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = ModeRx().Match(line.Trim());
            if (
                match.Success
                && int.TryParse(match.Groups["w"].Value, out var w)
                && int.TryParse(match.Groups["h"].Value, out var h)
                && seen.Add(new DisplayMode(w, h))
            )
            {
                modes.Add(new DisplayMode(w, h));
            }
        }
        return modes;
    }

    private static string? RunXrandr()
    {
        try
        {
            using var process = Process.Start(
                new ProcessStartInfo("xrandr", "--query")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                }
            );
            if (process is null)
            {
                return null;
            }
            var output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(5000) && process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            // No xrandr, no X display, or a sandbox that forbids spawning it: the mode list
            // still works, only the primary marker is lost.
            return null;
        }
    }

    [GeneratedRegex(@"^(?<name>\S+)\s+connected\s+primary\b", RegexOptions.Multiline)]
    private static partial Regex PrimaryRx();

    [GeneratedRegex(@"^(?<w>\d+)x(?<h>\d+)")]
    private static partial Regex ModeRx();
}
