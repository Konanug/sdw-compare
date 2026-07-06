using System.Windows;

namespace SolidWorksPartMatcher.App;

public partial class LauncherWindow : Window
{
    public LauncherWindow()
    {
        InitializeComponent();
    }

    private void Components_Click(object sender, RoutedEventArgs e)
    {
        var main = new MainWindow();
        System.Windows.Application.Current.MainWindow = main;
        main.Show();
        Close();
    }

    private void Assemblies_Click(object sender, RoutedEventArgs e)
    {
        var w = new AssemblyCompareWindow();
        System.Windows.Application.Current.MainWindow = w;
        w.Show();
        Close();
    }
}
