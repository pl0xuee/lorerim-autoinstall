using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Lorerim.Gui.Services.Modlist;

/// <summary>
/// Reads and writes Skyrim's render resolution in an installed modlist.
///
/// The target is each MO2 profile's skyrimprefs.ini, which BethINI generates as UTF-8 with a
/// BOM, CRLF terminators and keys spelled "iSize W =3840". Only the digits are replaced, so
/// encoding, line endings, comments and ordering survive a write byte-for-byte — rewriting
/// the file wholesale would normalise all of it for no benefit.
/// </summary>
public static partial class SkyrimResolutionService
{
    private const string PrefsName = "skyrimprefs.ini";
    private const string DisplaySection = "[Display]";

    /// <summary>The resolution the active profile is configured for, or null if unreadable.</summary>
    public static (int Width, int Height)? Read(string installDir)
    {
        var prefs = ProfilePrefs(installDir, ActiveProfile(installDir));
        if (prefs is null || !File.Exists(prefs))
        {
            return null;
        }
        string text;
        try
        {
            text = File.ReadAllText(prefs);
        }
        catch (IOException)
        {
            return null;
        }
        var w = SizeRx("W").Match(text);
        var h = SizeRx("H").Match(text);
        return w.Success
            && h.Success
            && int.TryParse(w.Groups["value"].Value, out var width)
            && int.TryParse(h.Groups["value"].Value, out var height)
            ? (width, height)
            : null;
    }

    /// <summary>
    /// Writes the resolution to every profile. Only the active one is read, but switching
    /// profile in MO2 must not silently revert the choice — which presents to a user as the
    /// setting simply not having worked.
    /// </summary>
    public static void Apply(string installDir, int width, int height)
    {
        var failures = new List<string>();
        foreach (var prefs in AllProfilePrefs(installDir))
        {
            try
            {
                ApplyToFile(prefs, width, height);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{Path.GetFileName(Path.GetDirectoryName(prefs))}: {ex.Message}");
            }
        }
        if (failures.Count > 0)
        {
            throw new IOException(
                "Could not write the resolution to every profile — "
                    + string.Join("; ", failures)
            );
        }
    }

    /// <summary>
    /// Parses a stored "WxH" preference. Anything malformed is rejected rather than guessed
    /// at: a bad value would be written into every profile, and the shipped LoreRim ini
    /// already carries a "25640x1440" typo showing how easily one appears.
    /// </summary>
    public static bool TryParse(string? stored, out (int Width, int Height) resolution)
    {
        resolution = default;
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }
        var match = ResolutionRx().Match(stored.Trim());
        if (
            !match.Success
            || !int.TryParse(match.Groups["w"].Value, out var w)
            || !int.TryParse(match.Groups["h"].Value, out var h)
            || w <= 0
            || h <= 0
        )
        {
            return false;
        }
        resolution = (w, h);
        return true;
    }

    /// <summary>
    /// Applies a stored preference, returning whether anything was written. No preference and
    /// an unparseable one both leave the install exactly as the modlist shipped it.
    /// </summary>
    public static bool ApplyPreference(string installDir, string? stored)
    {
        if (!TryParse(stored, out var resolution))
        {
            return false;
        }
        Apply(installDir, resolution.Width, resolution.Height);
        return true;
    }

    /// <summary>Profiles present in the install, whether or not they are active.</summary>
    public static IReadOnlyList<string> Profiles(string installDir)
    {
        var dir = Path.Join(installDir, "profiles");
        if (!Directory.Exists(dir))
        {
            return [];
        }
        return
        [
            .. Directory
                .GetDirectories(dir)
                .Where(d => File.Exists(Path.Join(d, PrefsName)))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.Ordinal),
        ];
    }

    private static void ApplyToFile(string prefs, int width, int height)
    {
        var bytes = File.ReadAllBytes(prefs);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.UTF8.GetString(hasBom ? bytes[3..] : bytes);
        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        text = ReplaceOrInsert(text, "W", width, eol);
        text = ReplaceOrInsert(text, "H", height, eol);

        var updated = Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(
            prefs,
            hasBom ? [.. new UTF8Encoding(true).GetPreamble(), .. updated] : updated
        );
    }

    /// <summary>
    /// Replaces the digits on an existing line, keeping the key's own spacing. A profile that
    /// has never had the key gets one inserted under [Display], where the game looks for it.
    /// </summary>
    private static string ReplaceOrInsert(string text, string axis, int value, string eol)
    {
        var rx = SizeRx(axis);
        if (rx.IsMatch(text))
        {
            return rx.Replace(text, m => m.Groups["key"].Value + value, 1);
        }
        var section = text.IndexOf(DisplaySection, StringComparison.OrdinalIgnoreCase);
        var line = $"iSize {axis} ={value}";
        if (section < 0)
        {
            return text + (text.EndsWith(eol, StringComparison.Ordinal) ? "" : eol)
                + DisplaySection + eol + line + eol;
        }
        var insertAt = text.IndexOf(eol, section, StringComparison.Ordinal);
        return insertAt < 0
            ? text + eol + line + eol
            : text.Insert(insertAt + eol.Length, line + eol);
    }

    private static string? ActiveProfile(string installDir)
    {
        var ini = Path.Join(installDir, "ModOrganizer.ini");
        if (!File.Exists(ini))
        {
            return null;
        }
        try
        {
            var match = SelectedProfileRx().Match(File.ReadAllText(ini));
            return match.Success ? match.Groups["name"].Value : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? ProfilePrefs(string installDir, string? profile) =>
        profile is null ? null : Path.Join(installDir, "profiles", profile, PrefsName);

    private static IEnumerable<string> AllProfilePrefs(string installDir) =>
        Profiles(installDir).Select(p => Path.Join(installDir, "profiles", p, PrefsName));

    /// <summary>Captures the key with its exact spacing so only the value is rewritten.</summary>
    private static Regex SizeRx(string axis) =>
        new($@"(?<key>iSize\s+{axis}\s*=\s*)(?<value>\d+)", RegexOptions.IgnoreCase);

    [GeneratedRegex(@"selected_profile\s*=\s*(?:@ByteArray\()?(?<name>[^)\r\n]+)\)?")]
    private static partial Regex SelectedProfileRx();

    [GeneratedRegex(@"^(?<w>\d+)\s*[xX]\s*(?<h>\d+)$")]
    private static partial Regex ResolutionRx();
}
