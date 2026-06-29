using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using SolidWorksPartMatcher.Infrastructure.Step;

using WCheckBox       = System.Windows.Controls.CheckBox;
using WEllipse        = System.Windows.Shapes.Ellipse;
using WTextBlock      = System.Windows.Controls.TextBlock;
using WStackPanel     = System.Windows.Controls.StackPanel;
using WMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using WMouseWheelEventArgs  = System.Windows.Input.MouseWheelEventArgs;
using WMouseButtonState     = System.Windows.Input.MouseButtonState;
using WColor          = System.Windows.Media.Color;
using WPoint          = System.Windows.Point;

namespace SolidWorksPartMatcher.App.Views;

/// <summary>
/// Single-viewport 3D diff window.  All selected STEP files are overlaid in the
/// same coordinate space (centroid-aligned).  Shared geometry is rendered as
/// semi-transparent grey; faces unique to each file appear in that file's colour.
/// </summary>
public partial class StepDiffWindow : Window
{
    // ── Colour palette — matches diff3d's pink/green aesthetic ────────────
    private static readonly WColor[] Palette =
    [
        WColor.FromRgb(0xFF, 0x40, 0x81),   // hot-pink
        WColor.FromRgb(0x00, 0xE5, 0x76),   // bright-green
        WColor.FromRgb(0xFF, 0x6D, 0x00),   // deep-orange
        WColor.FromRgb(0x40, 0xC4, 0xFF),   // light-blue
        WColor.FromRgb(0x69, 0xF0, 0xAE),   // teal
        WColor.FromRgb(0xFF, 0xD7, 0x40),   // amber
    ];

    // Shared geometry: semi-transparent dark-grey (similar to diff3d solid body)
    private static readonly WColor SharedColor = WColor.FromArgb(0xA8, 0x60, 0x70, 0x78);

    private readonly IReadOnlyList<string> _allPaths;
    private readonly List<(WCheckBox Cb, string Path, WColor Color)> _fileItems = [];

    // ── Camera orbit state ─────────────────────────────────────────────────
    private double _azimuth   = 30.0;
    private double _elevation = 25.0;
    private double _distance  = 1.0;
    private Point3D _center   = new(0, 0, 0);
    private WPoint  _dragStart;
    private bool    _isDragging;

    public StepDiffWindow(string displayName, IReadOnlyList<string> allPaths)
    {
        InitializeComponent();
        _allPaths = allPaths;
        TitleLabel.Text = $"3D Part Comparison — {displayName}";

        BuildFileSelector();

        // Defer first render until after WPF has fully initialised the Viewport3D
        Loaded += (_, _) => Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(RenderSelected));
    }

    // ── File selector ──────────────────────────────────────────────────────

    private void BuildFileSelector()
    {
        for (int i = 0; i < _allPaths.Count; i++)
        {
            var color = Palette[i % Palette.Length];

            var dot = new WEllipse
            {
                Width = 10, Height = 10,
                Fill  = new SolidColorBrush(color),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var lbl = new WTextBlock
            {
                Text = Path.GetFileName(_allPaths[i]),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 130
            };
            var row = new WStackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            row.Children.Add(dot);
            row.Children.Add(lbl);

            var cb = new WCheckBox
            {
                Content   = row,
                IsChecked = true,
                Margin    = new Thickness(0, 3, 0, 3)
            };

            _fileItems.Add((cb, _allPaths[i], color));
            FileSelectionPanel.Children.Add(cb);
        }
    }

    private void Compare_Click(object sender, RoutedEventArgs e) => RenderSelected();

    // ── Rendering ──────────────────────────────────────────────────────────

    private void RenderSelected()
    {
        var selected = _fileItems.Where(x => x.Cb.IsChecked == true).ToList();
        if (selected.Count < 2)
        {
            SummaryLabel.Text = "Select at least 2 files to compare.";
            return;
        }

        var parsedFiles = new List<(IReadOnlyList<StepFaceGeometry> Faces, WColor Color, string Name)>();
        try
        {
            foreach (var (_, path, color) in selected)
            {
                var reader  = StepP21Reader.ParseFile(path);
                var faces   = reader.GetFaceGeometries();
                var aligned = CentroidAlign(faces);
                parsedFiles.Add((aligned, color, Path.GetFileName(path)));
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to parse STEP file:\n{ex.Message}",
                "Parse Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        // Shared descriptors = appear in every selected file
        var sharedDescs = parsedFiles
            .Select(p => new HashSet<string>(p.Faces.Select(f => f.Descriptor), StringComparer.Ordinal))
            .Aggregate((a, b) => { a.IntersectWith(b); return a; });

        // Build a brand-new Model3DGroup each render (no reuse of frozen objects)
        var group = new Model3DGroup();

        // Pass 1: shared faces (from first file only — geometry is identical across files)
        foreach (var face in parsedFiles[0].Faces)
        {
            if (!sharedDescs.Contains(face.Descriptor)) continue;
            var mesh = BuildFaceMesh(face);
            if (mesh != null && mesh.Positions.Count > 0)
                group.Children.Add(MakeFaceModel(mesh, SharedColor));
        }

        // Pass 2: per-file unique faces (bright per-file colour)
        foreach (var (faces, color, _) in parsedFiles)
        {
            foreach (var face in faces)
            {
                if (sharedDescs.Contains(face.Descriptor)) continue;
                var mesh = BuildFaceMesh(face);
                if (mesh != null && mesh.Positions.Count > 0)
                    group.Children.Add(MakeFaceModel(mesh, color));
            }
        }

        // Clear then assign — prevents WPF from reusing stale frozen content
        PartsVisual.Content = null;
        PartsVisual.Content = group;

        // Fit camera to combined bounding box
        var allPoints = parsedFiles
            .SelectMany(p => p.Faces.SelectMany(f => f.BoundaryPoints))
            .ToList();
        FitCamera(allPoints);
        UpdateCamera();

        BuildLegend(parsedFiles.Select(p => (p.Name, p.Color)).ToList());

        int sharedCount = parsedFiles[0].Faces.Count(f => sharedDescs.Contains(f.Descriptor));
        int uniqueCount = parsedFiles.Sum(p => p.Faces.Count(f => !sharedDescs.Contains(f.Descriptor)));
        SummaryLabel.Text =
            $"Shared faces: {sharedCount}   " +
            $"Unique faces: {uniqueCount}   " +
            $"Files compared: {parsedFiles.Count}";
    }

    // ── Alignment ──────────────────────────────────────────────────────────

    private static IReadOnlyList<StepFaceGeometry> CentroidAlign(IReadOnlyList<StepFaceGeometry> faces)
    {
        if (faces.Count == 0) return faces;

        double mnX = double.MaxValue, mxX = double.MinValue;
        double mnY = double.MaxValue, mxY = double.MinValue;
        double mnZ = double.MaxValue, mxZ = double.MinValue;

        foreach (var f in faces)
        foreach (var p in f.BoundaryPoints)
        {
            if (p.Length < 3) continue;
            if (p[0] < mnX) mnX = p[0]; if (p[0] > mxX) mxX = p[0];
            if (p[1] < mnY) mnY = p[1]; if (p[1] > mxY) mxY = p[1];
            if (p[2] < mnZ) mnZ = p[2]; if (p[2] > mxZ) mxZ = p[2];
        }

        if (mnX > mxX) return faces;

        double cx = (mnX + mxX) / 2.0;
        double cy = (mnY + mxY) / 2.0;
        double cz = (mnZ + mxZ) / 2.0;

        return faces.Select(f => f with
        {
            BoundaryPoints = (IReadOnlyList<double[]>)f.BoundaryPoints
                .Select(p => p.Length >= 3 ? new[] { p[0] - cx, p[1] - cy, p[2] - cz } : p)
                .ToList(),
            AxisOrigin = f.AxisOrigin != null
                ? new[] { f.AxisOrigin[0] - cx, f.AxisOrigin[1] - cy, f.AxisOrigin[2] - cz }
                : null
        }).ToList();
    }

    // ── Mesh building ───────────────────────────────────────────────────────

    private static GeometryModel3D MakeFaceModel(MeshGeometry3D mesh, WColor color)
    {
        var mat = new DiffuseMaterial(new SolidColorBrush(color));
        return new GeometryModel3D(mesh, mat) { BackMaterial = mat };
    }

    private static MeshGeometry3D? BuildFaceMesh(StepFaceGeometry face) =>
        face.SurfaceType == "CYLINDRICAL_SURFACE"
            && face.AxisOrigin    != null
            && face.AxisDirection != null
            && face.Radius        != null
            ? BuildCylinderMesh(face.AxisOrigin, face.AxisDirection, face.Radius.Value, face.BoundaryPoints)
            : BuildPolygonMesh(face.BoundaryPoints);

    private static MeshGeometry3D BuildCylinderMesh(
        double[] axisOrigin, double[] axisDir, double radius,
        IReadOnlyList<double[]> boundaryPoints, int segments = 32)
    {
        double len = Math.Sqrt(axisDir[0] * axisDir[0] + axisDir[1] * axisDir[1] + axisDir[2] * axisDir[2]);
        if (len < 1e-10) return BuildPolygonMesh(boundaryPoints);

        double ax = axisDir[0] / len, ay = axisDir[1] / len, az = axisDir[2] / len;
        double ox = axisOrigin[0], oy = axisOrigin[1], oz = axisOrigin[2];

        double minT = double.MaxValue, maxT = double.MinValue;
        foreach (var p in boundaryPoints)
        {
            double t = (p[0] - ox) * ax + (p[1] - oy) * ay + (p[2] - oz) * az;
            if (t < minT) minT = t;
            if (t > maxT) maxT = t;
        }
        if (maxT - minT < 1e-10) { minT = -radius; maxT = radius; }

        double ux, uy, uz;
        if (Math.Abs(ax) < 0.9) { ux = 0;   uy =  az; uz = -ay; }
        else                     { ux = -az; uy =  0;  uz =  ax; }
        double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
        if (ul < 1e-10) ul = 1;
        ux /= ul; uy /= ul; uz /= ul;
        double vx = ay * uz - az * uy, vy = az * ux - ax * uz, vz = ax * uy - ay * ux;

        var mesh = new MeshGeometry3D();
        for (int i = 0; i <= segments; i++)
        {
            double theta = 2 * Math.PI * i / segments;
            double cos = Math.Cos(theta), sin = Math.Sin(theta);
            double nx = cos * ux + sin * vx, ny = cos * uy + sin * vy, nz = cos * uz + sin * vz;

            mesh.Positions.Add(new Point3D(
                ox + nx * radius + ax * minT,
                oy + ny * radius + ay * minT,
                oz + nz * radius + az * minT));
            mesh.Positions.Add(new Point3D(
                ox + nx * radius + ax * maxT,
                oy + ny * radius + ay * maxT,
                oz + nz * radius + az * maxT));
        }
        for (int i = 0; i < segments; i++)
        {
            int b0 = i * 2, t0 = b0 + 1, b1 = (i + 1) * 2, t1 = b1 + 1;
            mesh.TriangleIndices.Add(b0); mesh.TriangleIndices.Add(b1); mesh.TriangleIndices.Add(t0);
            mesh.TriangleIndices.Add(t0); mesh.TriangleIndices.Add(b1); mesh.TriangleIndices.Add(t1);
        }
        return mesh;
    }

    private static MeshGeometry3D BuildPolygonMesh(IReadOnlyList<double[]> vertices)
    {
        var mesh = new MeshGeometry3D();
        if (vertices.Count < 3) return mesh;

        double cx = 0, cy = 0, cz = 0;
        foreach (var v in vertices) { cx += v[0]; cy += v[1]; cz += v[2]; }
        cx /= vertices.Count; cy /= vertices.Count; cz /= vertices.Count;

        mesh.Positions.Add(new Point3D(cx, cy, cz));
        foreach (var v in vertices)
            mesh.Positions.Add(new Point3D(v[0], v[1], v[2]));

        int n = vertices.Count;
        for (int i = 0; i < n; i++)
        {
            mesh.TriangleIndices.Add(0);
            mesh.TriangleIndices.Add(i + 1);
            mesh.TriangleIndices.Add((i + 1) % n + 1);
        }
        return mesh;
    }

    // ── Legend ──────────────────────────────────────────────────────────────

    private void BuildLegend(IReadOnlyList<(string Name, WColor Color)> files)
    {
        LegendPanel.Children.Clear();
        AddLegendDot(SharedColor, "Shared geometry");
        foreach (var (name, color) in files)
            AddLegendDot(color, name);
    }

    private void AddLegendDot(WColor color, string label)
    {
        var sp = new WStackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 0, 18, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        sp.Children.Add(new WEllipse
        {
            Width = 12, Height = 12,
            Fill  = new SolidColorBrush(color),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        sp.Children.Add(new WTextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        LegendPanel.Children.Add(sp);
    }

    // ── Camera ──────────────────────────────────────────────────────────────

    private void FitCamera(IReadOnlyList<double[]> points)
    {
        if (points.Count == 0) { _distance = 0.5; return; }

        double mnX = double.MaxValue, mxX = double.MinValue;
        double mnY = double.MaxValue, mxY = double.MinValue;
        double mnZ = double.MaxValue, mxZ = double.MinValue;

        foreach (var p in points)
        {
            if (p.Length < 3) continue;
            if (p[0] < mnX) mnX = p[0]; if (p[0] > mxX) mxX = p[0];
            if (p[1] < mnY) mnY = p[1]; if (p[1] > mxY) mxY = p[1];
            if (p[2] < mnZ) mnZ = p[2]; if (p[2] > mxZ) mxZ = p[2];
        }

        if (mnX > mxX) { _distance = 0.5; return; }

        _center = new Point3D(
            (mnX + mxX) / 2.0,
            (mnY + mxY) / 2.0,
            (mnZ + mxZ) / 2.0);

        double span = Math.Max(mxX - mnX, Math.Max(mxY - mnY, mxZ - mnZ));
        _distance = Math.Max(span * 2.5, 1e-6);
    }

    private void UpdateCamera()
    {
        double az = _azimuth   * Math.PI / 180.0;
        double el = _elevation * Math.PI / 180.0;
        double dx = Math.Sin(az) * Math.Cos(el);
        double dy = Math.Sin(el);
        double dz = Math.Cos(az) * Math.Cos(el);

        var pos = new Point3D(
            _center.X + _distance * dx,
            _center.Y + _distance * dy,
            _center.Z + _distance * dz);

        MainCamera.Position      = pos;
        MainCamera.LookDirection = new Vector3D(
            _center.X - pos.X, _center.Y - pos.Y, _center.Z - pos.Z);
        MainCamera.UpDirection   = new Vector3D(0, 1, 0);
    }

    // ── Mouse orbit ─────────────────────────────────────────────────────────

    private void Viewport_MouseLeftButtonDown(object sender, WMouseButtonEventArgs e)
    {
        if (sender is not UIElement el) return;
        _dragStart  = e.GetPosition(el);
        _isDragging = true;
        el.CaptureMouse();
    }

    private void Viewport_MouseMove(object sender, WMouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != WMouseButtonState.Pressed) return;
        if (sender is not UIElement el) return;

        var pos = e.GetPosition(el);
        _azimuth   -= (pos.X - _dragStart.X) * 0.4;
        _elevation  = Math.Clamp(_elevation + (pos.Y - _dragStart.Y) * 0.4, -89.0, 89.0);
        _dragStart  = pos;
        UpdateCamera();
    }

    private void Viewport_MouseLeftButtonUp(object sender, WMouseButtonEventArgs e)
    {
        _isDragging = false;
        if (sender is UIElement el) el.ReleaseMouseCapture();
    }

    private void Viewport_MouseWheel(object sender, WMouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 0.85 : 1.0 / 0.85;
        _distance = Math.Max(_distance * factor, 1e-8);
        UpdateCamera();
    }

    // ── Buttons ─────────────────────────────────────────────────────────────

    private void ResetView_Click(object sender, RoutedEventArgs e)
    {
        _azimuth   = 30.0;
        _elevation = 25.0;
        UpdateCamera();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
