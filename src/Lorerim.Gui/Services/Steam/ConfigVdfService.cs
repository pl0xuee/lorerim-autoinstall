using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lorerim.Gui.Services.Steam;

/// <summary>
/// Sets the compatibility tool for an app by editing config/config.vdf as raw text
/// (Jackify NativeSteamService.set_proton_version port — text editing preserves Steam's
/// formatting). Key path: InstallConfigStore→Software→Valve→Steam→CompatToolMapping.
/// </summary>
public class ConfigVdfService
{
    public void SetCompatTool(SteamInstallation steam, long unsignedAppId, string toolName)
    {
        var path = steam.ConfigVdfPath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Steam config.vdf not found at {path}");
        }
        Backup(path);
        var text = File.ReadAllText(path);

        var entry =
            $"\t\t\t\t\t\"{unsignedAppId}\"\n"
            + "\t\t\t\t\t{\n"
            + $"\t\t\t\t\t\t\"name\"\t\t\"{toolName}\"\n"
            + "\t\t\t\t\t\t\"config\"\t\t\"\"\n"
            + "\t\t\t\t\t\t\"priority\"\t\t\"250\"\n"
            + "\t\t\t\t\t}\n";

        // Drop any existing block for this appid first (idempotent re-runs / tool changes).
        text = Regex.Replace(
            text,
            $"[ \\t]*\"{unsignedAppId}\"\\s*\\{{[^{{}}]*\\}}\\n?",
            ""
        );

        // VDF keys are case-insensitive; real config.vdf files vary in casing.
        var mappingIdx = text.IndexOf("\"CompatToolMapping\"", StringComparison.OrdinalIgnoreCase);
        if (mappingIdx >= 0)
        {
            var braceIdx = text.IndexOf('{', mappingIdx);
            if (braceIdx < 0)
            {
                throw new InvalidDataException("Malformed CompatToolMapping block in config.vdf");
            }
            text = text.Insert(braceIdx + 1, "\n" + entry);
        }
        else
        {
            // No CompatToolMapping yet — create it inside the "Steam" block.
            var steamIdx = text.IndexOf("\"Steam\"", StringComparison.OrdinalIgnoreCase);
            if (steamIdx < 0)
            {
                throw new InvalidDataException("No \"Steam\" block found in config.vdf");
            }
            var braceIdx = text.IndexOf('{', steamIdx);
            var block =
                "\n\t\t\t\t\"CompatToolMapping\"\n\t\t\t\t{\n" + entry + "\t\t\t\t}\n";
            text = text.Insert(braceIdx + 1, block);
        }

        AtomicFile.WriteAllText(path, text);
    }

    /// <summary>Reads back the tool name mapped for an appid, for post-restart verification.</summary>
    public string? GetCompatTool(SteamInstallation steam, long unsignedAppId)
    {
        if (!File.Exists(steam.ConfigVdfPath))
        {
            return null;
        }
        var text = File.ReadAllText(steam.ConfigVdfPath);
        var m = Regex.Match(
            text,
            $"\"{unsignedAppId}\"\\s*\\{{[^{{}}]*\"name\"\\s+\"(?<name>[^\"]+)\"",
            RegexOptions.Singleline
        );
        return m.Success ? m.Groups["name"].Value : null;
    }

    private static void Backup(string path)
    {
        var backupDir = Path.Join(Path.GetDirectoryName(path), "backups");
        Directory.CreateDirectory(backupDir);
        File.Copy(
            path,
            Path.Join(backupDir, $"config_{DateTimeOffset.Now.ToUnixTimeSeconds()}.bak"),
            true
        );
        foreach (
            var stale in new DirectoryInfo(backupDir)
                .GetFiles("config_*.bak")
                .OrderByDescending(f => f.Name)
                .Skip(5)
        )
        {
            stale.Delete();
        }
    }
}
