using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;

using WCheckBox = System.Windows.Controls.CheckBox;
using WEllipse = System.Windows.Shapes.Ellipse;
using WTextBlock = System.Windows.Controls.TextBlock;
using WStackPanel = System.Windows.Controls.StackPanel;
using WColor = System.Windows.Media.Color;

namespace SolidWorksPartMatcher.App.Views;

public partial class StepDiffWindow : Window
{
    // Must match PALETTE in tools/view_steps.py
    private static readonly WColor[] Palette =
    [
        WColor.FromRgb(0xFF, 0x40, 0x81),   // hot-pink
        WColor.FromRgb(0x00, 0xE5, 0x76),   // bright-green
        WColor.FromRgb(0xFF, 0x6D, 0x00),   // deep-orange
        WColor.FromRgb(0x40, 0xC4, 0xFF),   // light-blue
        WColor.FromRgb(0x69, 0xF0, 0xAE),   // teal
        WColor.FromRgb(0xFF, 0xD7, 0x40),   // amber
    ];

    private readonly IReadOnlyList<string> _allPaths;
    private readonly List<(WCheckBox Cb, string Path)> _fileItems = [];
    private readonly bool _useNativeAlignment;
    private readonly bool _sideBySide;

    // useNativeAlignment: skip the viewer's per-file centroid re-centering and overlay files in
    // their own shared coordinate system instead. Defaults to false (existing behavior,
    // unchanged) — pass true only when the files are known to share a common coordinate system
    // (e.g. two versions of the same assembly), where centroid re-centering would discard a
    // correct native alignment and replace it with a misleading one. See tools/view_steps.py.
    //
    // sideBySide: show each file in its own independent viewport (divided by a visible border)
    // instead of overlaying them — sidesteps the alignment problem entirely (no registration
    // attempt, nothing to misalign) and each viewport's camera is independent, so rotating one
    // part never affects the other. Requires exactly 2 files; ignored otherwise. Defaults to
    // false (existing overlay behavior, unchanged).
    public StepDiffWindow(
        string displayName, IReadOnlyList<string> allPaths,
        bool useNativeAlignment = false, bool sideBySide = false)
    {
        InitializeComponent();
        _allPaths = allPaths;
        _useNativeAlignment = useNativeAlignment;
        _sideBySide = sideBySide;
        TitleLabel.Text = $"3D Part Comparison — {displayName}";
        BuildFileSelector();
    }

    // ── File selector ──────────────────────────────────────────────────────

    private void BuildFileSelector()
    {
        for (int i = 0; i < _allPaths.Count; i++)
        {
            var color = Palette[i % Palette.Length];

            var dot = new WEllipse
            {
                Width = 10,
                Height = 10,
                Fill = new SolidColorBrush(color),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var lbl = new WTextBlock
            {
                Text = Path.GetFileName(_allPaths[i]),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 185
            };
            var row = new WStackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal
            };
            row.Children.Add(dot);
            row.Children.Add(lbl);

            var cb = new WCheckBox
            {
                Content = row,
                IsChecked = true,
                Margin = new Thickness(0, 3, 0, 3)
            };

            _fileItems.Add((cb, _allPaths[i]));
            FileSelectionPanel.Children.Add(cb);
        }
    }

    // ── Compare button ──────────────────────────────────────────────────────

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        var selected = _fileItems
            .Where(x => x.Cb.IsChecked == true)
            .Select(x => x.Path)
            .ToList();

        if (selected.Count < 1)
        {
            SummaryLabel.Text = "Select at least 1 file to view.";
            return;
        }

        var (exe, script) = FindViewer();
        if (string.IsNullOrEmpty(exe))
        {
            System.Windows.MessageBox.Show(
                "The 3D viewer component is missing.\n\n" +
                "If you are using a release build, re-download and ensure the viewer\\ folder " +
                "is present alongside the application.\n\n" +
                "If you are running from source, run tools\\build_viewer.ps1 first.",
                "Viewer Not Found",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        CompareButton.IsEnabled = false;
        LoadingBar.Visibility = Visibility.Visible;
        SummaryLabel.Text = "Loading… tessellating parts, please wait.";

        // Build the command line
        string flagArg = (_useNativeAlignment ? "--native-align " : "")
                        + (_sideBySide && selected.Count == 2 ? "--side-by-side " : "");
        string fileArgs = string.Join(" ", selected.Select(p => $"\"{p}\""));
        string allArgs = script is null
            ? $"{flagArg}{fileArgs}"                         // bundled: view_steps.exe [--native-align] "f1" "f2"
            : $"\"{script}\" {flagArg}{fileArgs}";           // dev:     python "script.py" [--native-align] "f1" "f2"

        // UseShellExecute=false + CreateNoWindow=true: no console window ever appears.
        // RedirectStandardOutput lets us detect when pyvista is ready to render.
        // RedirectStandardError drains VTK noise silently so the pipe never blocks.
        var psi = new ProcessStartInfo(exe, allArgs)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        try
        {
            var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");

            // Drain stderr on a background thread so VTK messages never block the pipe
            _ = Task.Run(() => { try { process.StandardError.ReadToEnd(); } catch { } });

            // Watch stdout for PYVISTA_READY; drain the rest silently while pyvista runs
            var readyTcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _ = Task.Run(() =>
            {
                try
                {
                    string? line;
                    while ((line = process.StandardOutput.ReadLine()) != null)
                    {
                        if (line.TrimEnd() == "PYVISTA_READY")
                        {
                            readyTcs.TrySetResult(true);
                            // Keep draining so the pipe never fills and blocks the viewer
                            while (process.StandardOutput.ReadLine() != null) { }
                            return;
                        }
                    }
                    readyTcs.TrySetResult(false); // EOF before ready → script errored
                }
                catch { readyTcs.TrySetResult(false); }
            });

            bool ready = await readyTcs.Task;

            if (ready)
            {
                // The 3D window is up — this file-selector window has done its job, so close it
                // instead of leaving it sitting behind/alongside the real viewer.
                Close();
                return;
            }

            SummaryLabel.Text = "Viewer exited before opening — check that pyvista and build123d are installed.";
        }
        catch (Exception ex)
        {
            SummaryLabel.Text = "Failed to launch viewer.";
            System.Windows.MessageBox.Show(
                $"Could not launch the 3D viewer:\n{ex.Message}",
                "Launch Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            CompareButton.IsEnabled = true;
        }
    }

    // ── Viewer location ─────────────────────────────────────────────────────

    // Returns (exe, script):
    //   • Bundled (production): exe = "…\viewer\view_steps.exe", script = null
    //   • Development fallback: exe = "python",                  script = "…\tools\view_steps.py"
    //   • Not found:            exe = "",                        script = null
    private static (string Exe, string? Script) FindViewer()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            // Production: PyInstaller bundle copied in by publish.ps1
            string bundled = Path.Combine(dir.FullName, "viewer", "view_steps.exe");
            if (File.Exists(bundled)) return (bundled, null);

            // Development: Python must be on PATH, packages must be installed
            string devScript = Path.Combine(dir.FullName, "tools", "view_steps.py");
            if (File.Exists(devScript)) return ("python", devScript);

            dir = dir.Parent;
        }
        return (string.Empty, null);
    }

    // ── Buttons ─────────────────────────────────────────────────────────────

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
