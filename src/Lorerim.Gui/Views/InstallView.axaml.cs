using Avalonia.Controls;
using Lorerim.Gui.ViewModels;

namespace Lorerim.Gui.Views;

public partial class InstallView : UserControl
{
    public InstallView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
            (DataContext as InstallViewModel)?.RefreshAuthStatus();
    }
}
