using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SolidWorksPartMatcher.App.ViewModels;
using SolidWorksPartMatcher.Application.Interfaces;

namespace SolidWorksPartMatcher.App;

public partial class AssemblyCompareWindow : Window
{
    private const string FileFilter = "STEP files (*.step;*.stp)|*.step;*.stp";

    private string? _pathA;
    private string? _pathB;

    public AssemblyCompareWindow()
    {
        InitializeComponent();
    }

    private void BrowseA_Click(object sender, RoutedEventArgs e)
    {
        var path = PromptForFile("Select Assembly A");
        if (path is null) return;
        _pathA = path;
        FileABox.Text = path;
        StatusText.Text = "";
    }

    private void BrowseB_Click(object sender, RoutedEventArgs e)
    {
        var path = PromptForFile("Select Assembly B");
        if (path is null) return;
        _pathB = path;
        FileBBox.Text = path;
        StatusText.Text = "";
    }

    private static string? PromptForFile(string title)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = FileFilter
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_pathA is null || _pathB is null)
        {
            StatusText.Text = "Select both assembly files before comparing.";
            return;
        }

        if (string.Equals(_pathA, _pathB, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Select two different files to compare.";
            return;
        }

        CompareButton.IsEnabled = false;
        LoadingBar.Visibility = Visibility.Visible;
        StatusText.Foreground = System.Windows.Media.Brushes.Gray;
        StatusText.Text = "Parsing and comparing assemblies…";

        try
        {
            var orchestrator = App.Services.GetRequiredService<IAssemblyDiffOrchestrationService>();
            var summary = await orchestrator.CompareAsync(_pathA, _pathB, null, null, CancellationToken.None);

            var vm = new AssemblyDiffResultsViewModel(
                summary, _pathA, _pathB,
                App.Services.GetRequiredService<IAssemblyDiffReportExporter>(),
                App.Services.GetRequiredService<ILogger<AssemblyDiffResultsViewModel>>());

            var win = new Views.AssemblyDiffResultsWindow(vm) { Owner = this };
            win.Show();
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = $"Comparison failed: {ex.Message}";
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            CompareButton.IsEnabled = true;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        var launcher = new LauncherWindow();
        System.Windows.Application.Current.MainWindow = launcher;
        launcher.Show();
        Close();
    }
}
