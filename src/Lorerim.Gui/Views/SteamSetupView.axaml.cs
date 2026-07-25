using Avalonia.Controls;
using Lorerim.Gui.ViewModels;

namespace Lorerim.Gui.Views;

public partial class SteamSetupView : UserControl
{
    public SteamSetupView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is SteamSetupViewModel vm)
            {
                vm.EnsureCheckedOnce();
            }
        };
    }
}
