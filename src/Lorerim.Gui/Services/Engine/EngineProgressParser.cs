using System;
using System.Text.RegularExpressions;

namespace Lorerim.Gui.Services.Engine;

/// <summary>
/// Parses jackify-engine stdout lines. Format (from Jackify's progress_parser_files.py):
/// [FILE_PROGRESS] Downloading: SomeMod.7z (42.3%) [12.3MB/s] (17/4402)
/// Speed and counter groups are optional.
/// </summary>
public static partial class EngineProgressParser
{
    [GeneratedRegex(
        @"\[FILE_PROGRESS\]\s+(Downloading|Extracting|Validating|Installing|Converting|Building|Writing|Verifying|Completed|Checking existing):\s+(.+?)\s+\((\d+(?:\.\d+)?)%\)\s*(?:\[(.+?)\])?\s*(?:\((\d+)/(\d+)\))?",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex FileProgressRegex();

    public static EngineFileProgress? TryParse(string line)
    {
        if (string.IsNullOrEmpty(line) || line.Length > 10000)
        {
            return null;
        }
        var m = FileProgressRegex().Match(line);
        if (!m.Success)
        {
            return null;
        }
        var op = m.Groups[1].Value.ToLowerInvariant() switch
        {
            "downloading" => EngineOperation.Downloading,
            "extracting" => EngineOperation.Extracting,
            "validating" => EngineOperation.Validating,
            "installing" => EngineOperation.Installing,
            "converting" => EngineOperation.Converting,
            "building" => EngineOperation.Building,
            "writing" => EngineOperation.Writing,
            "verifying" => EngineOperation.Verifying,
            "checking existing" => EngineOperation.CheckingExisting,
            "completed" => EngineOperation.Completed,
            _ => EngineOperation.Unknown,
        };
        var percent = double.Parse(
            m.Groups[3].Value,
            System.Globalization.CultureInfo.InvariantCulture
        );
        if (op == EngineOperation.Completed)
        {
            percent = 100.0;
        }
        return new EngineFileProgress(
            op,
            m.Groups[2].Value.Trim(),
            percent,
            m.Groups[4].Success ? m.Groups[4].Value.Trim() : null,
            m.Groups[5].Success ? int.Parse(m.Groups[5].Value) : null,
            m.Groups[6].Success ? int.Parse(m.Groups[6].Value) : null
        );
    }
}
