using Microsoft.Extensions.DependencyInjection;
using SolidWorksPartMatcher.App.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace SolidWorksPartMatcher.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<MainViewModel>();
    }

    private void BackToLauncher_Click(object sender, RoutedEventArgs e)
    {
        var launcher = new LauncherWindow();
        launcher.Show();
        Close();
    }

    // Opens the group ContextMenu when the ⋮ button is clicked.
    // Stops event propagation so TreeViewItem selection/expansion is not triggered.
    private void GroupActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.ContextMenu is { } menu)
        {
            menu.PlacementTarget = btn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
        e.Handled = true;
    }
}
