using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lorerim.Gui.Services;

/// <summary>Minimal modal yes/cancel dialog for destructive actions.</summary>
public static class ConfirmDialog
{
    public static async Task<bool> ShowAsync(string title, string message)
    {
        if (
            Avalonia.Application.Current?.ApplicationLifetime
                is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null
        )
        {
            return false;
        }

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#131820")),
        };

        var confirm = new Button
        {
            Content = "Delete",
            Classes = { "accent" },
        };
        confirm.Click += (_, _) => dialog.Close(true);
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => dialog.Close(false);

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, confirm },
                },
            },
        };

        return await dialog.ShowDialog<bool?>(desktop.MainWindow) == true;
    }
}
