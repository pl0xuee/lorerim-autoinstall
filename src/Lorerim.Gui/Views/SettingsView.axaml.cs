using Avalonia.Controls;
using Lorerim.Gui.ViewModels;

namespace Lorerim.Gui.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                vm.ReloadShared();
                vm.RefreshAuthState();
            }
        };
    }
}
