using System.Threading.Tasks;
using Lorerim.Gui.Models;

namespace Lorerim.Gui.Services;

/// <summary>Owns settings.json (~/.config/lorerim-autoinstall/).</summary>
public class SettingsService
{
    public AppSettings Settings { get; private set; } = AppSettings.Load();

    public Task SaveAsync() => Settings.SaveAsync();

    public void Reload() => Settings = AppSettings.Load();
}
