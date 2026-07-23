using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Lorerim.Gui.Models;

public class AppSettings
{
    public string InstallDir { get; set; } = DefaultInstallDir;
    public string DownloadDir { get; set; } = DefaultDownloadDir;

    /// <summary>Local .wabbajack file; when set it takes precedence over the machine URL.</summary>
    public string? WabbajackFilePath { get; set; }

    /// <summary>Wabbajack gallery machine URL; null means "resolve LoreRim automatically".</summary>
    public string? MachineUrlOverride { get; set; }

    /// <summary>Internal name of the preferred compat tool (e.g. GE-Proton10-4); null = auto-pick.</summary>
    public string? PreferredProtonInternalName { get; set; }

    /// <summary>Legacy Nexus API key fallback when OAuth is not used.</summary>
    public string? NexusApiKey { get; set; }

    /// <summary>
    /// Render resolution to write into the modlist as "WxH". Null means "leave whatever the
    /// modlist ships", so an install nobody has configured is never touched.
    /// </summary>
    public string? PreferredResolution { get; set; }

    public bool SetupSteamAfterInstall { get; set; } = true;

    public async Task SaveAsync()
    {
        if (!Directory.Exists(AppDataPath))
        {
            // 0700: this directory holds the Nexus API key and OAuth token.
            Directory.CreateDirectory(
                AppDataPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
        // settings.json can carry the Nexus API key — keep it owner-only like the token file.
        await Services.AtomicFile.WriteAllTextAsync(
            SettingsPath,
            JsonSerializer.Serialize(this, AppSettingsCtx.Default.AppSettings),
            unixCreateMode: UnixFileMode.UserRead | UnixFileMode.UserWrite
        );
    }

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }
        // Tighten permissions left behind by older versions that wrote 0644/0755.
        try
        {
            File.SetUnixFileMode(SettingsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(
                AppDataPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
        catch (IOException)
        {
            // best effort
        }
        try
        {
            return JsonSerializer.Deserialize(
                    File.ReadAllText(SettingsPath),
                    AppSettingsCtx.Default.AppSettings
                ) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // A corrupt settings.json must not brick the app; keep the broken file for
            // inspection and start fresh in memory (only persisted if the user saves).
            File.Copy(SettingsPath, SettingsPath + ".corrupt", true);
            return new AppSettings();
        }
    }

    public static readonly string AppDataPath = Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "lorerim-autoinstall"
    );

    public static string SettingsPath => Path.Join(AppDataPath, "settings.json");

    /// <summary>Expands a leading ~/ so a hand-edited settings file works like a shell path.</summary>
    public static string ExpandHome(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;

    private static string DefaultInstallDir =>
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", "LoreRim");

    private static string DefaultDownloadDir =>
        Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Games",
            "LoreRim-downloads"
        );
}

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class AppSettingsCtx : JsonSerializerContext;
