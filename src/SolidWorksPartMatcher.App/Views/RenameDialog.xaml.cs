using System.Windows;

namespace SolidWorksPartMatcher.App.Views;

public partial class RenameDialog : Window
{
    public string NewName
    {
        get => NameBox.Text;
        set => NameBox.Text = value;
    }

    public RenameDialog() => InitializeComponent();

    private void OkClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            System.Windows.MessageBox.Show("Name cannot be empty.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
