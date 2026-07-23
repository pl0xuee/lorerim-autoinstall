using System.Collections.Specialized;
using Avalonia.Controls;
using Lorerim.Gui.ViewModels;

namespace Lorerim.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Follow the log tail: without this, opening the pane after a failure shows the
        // oldest of up to 2000 lines and the actual error sits hours down the scrollbar.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm)
            {
                return;
            }
            vm.LogLines.CollectionChanged += OnLogChanged;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.LogPaneOpen) && vm.LogPaneOpen)
                {
                    ScrollLogToEnd();
                }
            };
        };
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (LogList.IsVisible)
        {
            ScrollLogToEnd();
        }
    }

    private void ScrollLogToEnd()
    {
        // Deferred so it runs after layout when the pane has only just become visible.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is MainViewModel { LogLines.Count: > 0 } vm)
            {
                LogList.ScrollIntoView(vm.LogLines[^1]);
            }
        });
    }
}
