using System.Windows;
using SolidWorksPartMatcher.App.ViewModels;

namespace SolidWorksPartMatcher.App.Views;

public partial class AssemblyDiffResultsWindow : Window
{
    public AssemblyDiffResultsWindow(AssemblyDiffResultsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
