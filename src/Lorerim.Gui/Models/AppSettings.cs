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

    public bool SetupSteamAfterInstall { get; set; } = true;

    public bool ShownFirstLaunchGuide { get; set; }

    public async Task SaveAsync()
    {
        if (!Directory.Exists(AppDataPath))
        {
            Directory.CreateDirectory(AppDataPath);
        }
        await Services.AtomicFile.WriteAllTextAsync(
            SettingsPath,
            JsonSerializer.Serialize(this, AppSettingsCtx.Default.AppSettings)
        );
    }

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
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
