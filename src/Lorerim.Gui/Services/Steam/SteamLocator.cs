using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lorerim.Gui.Services.Steam;

public sealed record SteamInstallation(string Root, string UserId)
{
    public string UserConfigDir => Path.Join(Root, "userdata", UserId, "config");
    public string ShortcutsVdfPath => Path.Join(UserConfigDir, "shortcuts.vdf");
    public string ConfigVdfPath => Path.Join(Root, "config", "config.vdf");
    public string CompatDataDir => Path.Join(Root, "steamapps", "compatdata");
}

/// <summary>
/// Finds the native Steam installation and the most recent user (Jackify's
/// steam_utils/find_steam_user logic). Flatpak Steam is intentionally unsupported.
/// </summary>
public partial class SteamLocator
{
    public SteamInstallation? Locate()
    {
        var root = CandidateRoots()
            .Select(ResolvePath)
            .FirstOrDefault(r =>
                r is not null
                && File.Exists(Path.Join(r, "config", "loginusers.vdf"))
                && Directory.Exists(Path.Join(r, "userdata"))
            );
        if (root is null)
        {
            return null;
        }
        var userId = FindMostRecentUser(root);
        return userId is null ? null : new SteamInstallation(root, userId);
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Join(home, ".steam", "steam");
        yield return Path.Join(home, ".local", "share", "Steam");
        yield return Path.Join(home, ".steam", "root");
    }

    private static string? ResolvePath(string path)
    {
        try
        {
            return Directory.Exists(path) ? new DirectoryInfo(path).ResolveLinkTarget(true)?.FullName ?? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>MostRecent=1 in loginusers.vdf, else highest Timestamp; steamid3 = steamid64 - 76561197960265728.</summary>
    private static string? FindMostRecentUser(string root)
    {
        var loginusers = Path.Join(root, "config", "loginusers.vdf");
        var text = File.ReadAllText(loginusers);
        var users = new List<(long Id64, bool MostRecent, long Timestamp)>();
        foreach (Match block in UserBlockRx().Matches(text))
        {
            var id64 = long.Parse(block.Groups["id"].Value);
            var body = block.Groups["body"].Value;
            var mostRecent = Regex.IsMatch(body, "\"MostRecent\"\\s+\"1\"");
            var tsMatch = Regex.Match(body, "\"Timestamp\"\\s+\"(?<ts>\\d+)\"");
            var ts = tsMatch.Success ? long.Parse(tsMatch.Groups["ts"].Value) : 0;
            users.Add((id64, mostRecent, ts));
        }

        var chosen = users.FirstOrDefault(u => u.MostRecent);
        if (chosen == default)
        {
            chosen = users.OrderByDescending(u => u.Timestamp).FirstOrDefault();
        }
        if (chosen != default)
        {
            var steamId3 = chosen.Id64 - 76561197960265728L;
            if (Directory.Exists(Path.Join(root, "userdata", steamId3.ToString())))
            {
                return steamId3.ToString();
            }
        }

        // Fallback: newest numeric userdata dir (excluding 0).
        return new DirectoryInfo(Path.Join(root, "userdata"))
            .GetDirectories()
            .Where(d => long.TryParse(d.Name, out var n) && n != 0)
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .FirstOrDefault()
            ?.Name;
    }

    [GeneratedRegex("\"(?<id>7656119\\d+)\"\\s*\\{(?<body>[^}]*)\\}", RegexOptions.Singleline)]
    private static partial Regex UserBlockRx();
}
