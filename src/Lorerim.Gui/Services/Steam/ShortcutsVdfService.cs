using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Lorerim.Gui.Services.Steam;

public sealed record SteamShortcut(int SignedAppId, string AppName, string Exe, string StartDir)
{
    public long UnsignedAppId => SignedAppId < 0 ? SignedAppId + 4294967296L : SignedAppId;
}

/// <summary>
/// Reads/writes Steam's binary shortcuts.vdf (Jackify NativeSteamService port).
/// AppIDs are random negative signed 32-bit ints; the unsigned form keys CompatToolMapping
/// and steamapps/compatdata.
/// </summary>
public class ShortcutsVdfService
{
    /// <summary>Returns (appName, exe) pairs of existing shortcuts.</summary>
    public List<SteamShortcut> List(SteamInstallation steam)
    {
        var path = steam.ShortcutsVdfPath;
        if (!File.Exists(path))
        {
            return [];
        }
        var root = BinaryVdf.Read(File.ReadAllBytes(path));
        return Entries(root)
            .Select(e => new SteamShortcut(
                e.TryGetValue("appid", out var id) && id is int i ? i : 0,
                Str(e, "AppName"),
                Str(e, "Exe"),
                Str(e, "StartDir")
            ))
            .ToList();
    }

    /// <summary>
    /// Adds (or replaces, matching on AppName) a shortcut. Returns the shortcut with its appid.
    /// Steam must not be running while this is written.
    /// </summary>
    public SteamShortcut Upsert(
        SteamInstallation steam,
        string appName,
        string exePath,
        string startDir,
        string launchOptions,
        string? iconPath = null
    )
    {
        var path = steam.ShortcutsVdfPath;
        Dictionary<string, object> root;
        if (File.Exists(path))
        {
            Backup(path);
            root = BinaryVdf.Read(File.ReadAllBytes(path));
        }
        else
        {
            root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }
        if (root.TryGetValue("shortcuts", out var s) && s is Dictionary<string, object> existing)
        {
            // keep
        }
        else
        {
            existing = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            root["shortcuts"] = existing;
        }

        // Replace an existing shortcut with the same AppName (keep its appid for idempotent re-runs).
        var signedAppId = GenerateSignedAppId();
        var existingKey = existing
            .FirstOrDefault(kv =>
                kv.Value is Dictionary<string, object> d
                && string.Equals(Str(d, "AppName"), appName, StringComparison.OrdinalIgnoreCase)
            )
            .Key;
        // Reuse ANY existing non-zero appid (some tools write positive ids) so re-runs and
        // takeovers keep pointing at the same compatdata prefix and CompatToolMapping entry.
        if (existingKey is not null && existing[existingKey] is Dictionary<string, object> old
            && old.TryGetValue("appid", out var oldId) && oldId is int oldSigned && oldSigned != 0)
        {
            signedAppId = oldSigned;
        }

        // Start from the existing entry (idempotent rerun / takeover) so the user's own edits
        // survive — LastPlayTime, custom artwork, collection tags, overlay tweaks. Only the
        // fields we own are (re)written. New shortcuts get sensible defaults.
        var entry =
            existingKey is not null && existing[existingKey] is Dictionary<string, object> prior
                ? prior
                : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ShortcutPath"] = "",
                    ["IsHidden"] = 0,
                    ["AllowDesktopConfig"] = 1,
                    ["AllowOverlay"] = 1,
                    ["OpenVR"] = 0,
                    ["Devkit"] = 0,
                    ["DevkitGameID"] = "",
                    ["DevkitOverrideAppID"] = 0,
                    ["LastPlayTime"] = 0,
                    ["IsInstalled"] = 1,
                    ["FlatpakAppID"] = "",
                    ["tags"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["0"] = "LorerimAutoinstall",
                    },
                };

        entry["appid"] = signedAppId;
        entry["AppName"] = appName;
        entry["Exe"] = $"\"{exePath}\"";
        entry["StartDir"] = $"\"{startDir}\"";
        entry["LaunchOptions"] = launchOptions;
        // Only set the icon if we have one; don't clobber a user-set custom icon with "".
        if (!string.IsNullOrEmpty(iconPath))
        {
            entry["icon"] = iconPath;
        }
        else
        {
            entry.TryAdd("icon", "");
        }

        var key =
            existingKey
            ?? (existing.Keys.Select(k => int.TryParse(k, out var n) ? n : -1).DefaultIfEmpty(-1).Max() + 1).ToString();
        existing[key] = entry;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllBytes(path, BinaryVdf.Write(root));
        return new SteamShortcut(signedAppId, appName, exePath, startDir);
    }

    private static IEnumerable<Dictionary<string, object>> Entries(Dictionary<string, object> root) =>
        root.TryGetValue("shortcuts", out var s) && s is Dictionary<string, object> map
            ? map.Values.OfType<Dictionary<string, object>>()
            : [];

    private static string Str(Dictionary<string, object> d, string key) =>
        d.TryGetValue(key, out var v) && v is string s ? s.Trim('"') : "";

    private static int GenerateSignedAppId() =>
        -RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);

    private static void Backup(string path)
    {
        var backupDir = Path.Join(Path.GetDirectoryName(path), "backups");
        Directory.CreateDirectory(backupDir);
        File.Copy(
            path,
            Path.Join(backupDir, $"shortcuts_{DateTimeOffset.Now.ToUnixTimeSeconds()}.bak"),
            true
        );
        foreach (
            var stale in new DirectoryInfo(backupDir)
                .GetFiles("shortcuts_*.bak")
                .OrderByDescending(f => f.Name)
                .Skip(5)
        )
        {
            stale.Delete();
        }
    }
}
