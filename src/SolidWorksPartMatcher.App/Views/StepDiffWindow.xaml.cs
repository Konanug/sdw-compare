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
using WMouseButton          = System.Windows.Input.MouseButton;
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
    private double  _azimuth      = 30.0;
    private double  _elevation    = 25.0;
    private double  _distance     = 1.0;
    private Point3D _center       = new(0, 0, 0);
    private WPoint  _lastMousePos;
    private bool    _isOrbitDragging;

    public StepDiffWindow(string displayName, IReadOnlyList<string> allPaths)
    {
        InitializeComponent();
        _allPaths = allPaths;
        TitleLabel.Text = $"3D Part Comparison — {displayName}";

        BuildFileSelector();

        // Window-level mouse: orbit works from anywhere in the window, not just
        // when hovering the Viewport3D.  Also handles middle-button drag (SolidWorks style).
        PreviewMouseDown  += OnPreviewMouseDown;
        PreviewMouseMove  += OnPreviewMouseMove;
        PreviewMouseUp    += OnPreviewMouseUp;
        PreviewMouseWheel += OnPreviewMouseWheel;

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

        // Build a brand-new Model3DGroup each render (no reuse of frozen objects).
        // Opaque unique faces are added FIRST so depth-testing is correct before
        // the semi-transparent shared faces are composited on top.
        var group = new Model3DGroup();

        // Pass 1: per-file unique faces (opaque) — must be in depth buffer before transparents
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

        // Pass 2: shared faces (semi-transparent grey, from first file — geometry identical)
        foreach (var face in parsedFiles[0].Faces)
        {
            if (!sharedDescs.Contains(face.Descriptor)) continue;
            var mesh = BuildFaceMesh(face);
            if (mesh != null && mesh.Positions.Count > 0)
                group.Children.Add(MakeFaceModel(mesh, SharedColor));
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

    private static MeshGeometry3D? BuildFaceMesh(StepFaceGeometry face) => face.SurfaceType switch
    {
        "CYLINDRICAL_SURFACE" when face.AxisOrigin != null && face.AxisDirection != null && face.Radius != null
            => BuildCylinderMesh(face.AxisOrigin, face.AxisDirection, face.Radius.Value, face.BoundaryPoints),
        "CONICAL_SURFACE" when face.AxisOrigin != null && face.AxisDirection != null
            => BuildFrustumMesh(face.AxisOrigin, face.AxisDirection, face.BoundaryPoints),
        _   => BuildPolygonMesh(face.BoundaryPoints, face.AxisDirection)
    };

    private static MeshGeometry3D BuildCylinderMesh(
        double[] axisOrigin, double[] axisDir, double radius,
        IReadOnlyList<double[]> boundaryPoints, int segments = 40)
    {
        double len = Math.Sqrt(axisDir[0]*axisDir[0] + axisDir[1]*axisDir[1] + axisDir[2]*axisDir[2]);
        if (len < 1e-10) return BuildPolygonMesh(boundaryPoints);

        double ax = axisDir[0]/len, ay = axisDir[1]/len, az = axisDir[2]/len;
        double ox = axisOrigin[0], oy = axisOrigin[1], oz = axisOrigin[2];

        double minT = double.MaxValue, maxT = double.MinValue;
        foreach (var p in boundaryPoints)
        {
            double t = (p[0]-ox)*ax + (p[1]-oy)*ay + (p[2]-oz)*az;
            if (t < minT) minT = t;
            if (t > maxT) maxT = t;
        }
        if (maxT - minT < 1e-10) { minT = -radius; maxT = radius; }

        double ux, uy, uz;
        if (Math.Abs(ax) < 0.9) { ux = 0; uy = az; uz = -ay; }
        else                     { ux = -az; uy = 0; uz = ax; }
        double ul = Math.Sqrt(ux*ux + uy*uy + uz*uz);
        if (ul < 1e-10) ul = 1;
        ux /= ul; uy /= ul; uz /= ul;
        double vx = ay*uz - az*uy, vy = az*ux - ax*uz, vz = ax*uy - ay*ux;

        var mesh = new MeshGeometry3D();
        for (int i = 0; i <= segments; i++)
        {
            double theta = 2 * Math.PI * i / segments;
            double cos = Math.Cos(theta), sin = Math.Sin(theta);
            double nx = cos*ux + sin*vx, ny = cos*uy + sin*vy, nz = cos*uz + sin*vz;

            mesh.Positions.Add(new Point3D(ox + nx*radius + ax*minT,
                                           oy + ny*radius + ay*minT,
                                           oz + nz*radius + az*minT));
            mesh.Positions.Add(new Point3D(ox + nx*radius + ax*maxT,
                                           oy + ny*radius + ay*maxT,
                                           oz + nz*radius + az*maxT));
        }
        for (int i = 0; i < segments; i++)
        {
            int b0 = i*2, t0 = b0+1, b1 = (i+1)*2, t1 = b1+1;
            mesh.TriangleIndices.Add(b0); mesh.TriangleIndices.Add(b1); mesh.TriangleIndices.Add(t0);
            mesh.TriangleIndices.Add(t0); mesh.TriangleIndices.Add(b1); mesh.TriangleIndices.Add(t1);
        }

        // Closed disk caps — the cylinder was previously open at both ends
        int botIdx = mesh.Positions.Count;
        mesh.Positions.Add(new Point3D(ox + ax*minT, oy + ay*minT, oz + az*minT));
        int topIdx = mesh.Positions.Count;
        mesh.Positions.Add(new Point3D(ox + ax*maxT, oy + ay*maxT, oz + az*maxT));
        for (int i = 0; i < segments; i++)
        {
            int b0 = i*2, b1 = (i+1)*2, t0 = i*2+1, t1 = (i+1)*2+1;
            mesh.TriangleIndices.Add(botIdx); mesh.TriangleIndices.Add(b0); mesh.TriangleIndices.Add(b1);
            mesh.TriangleIndices.Add(topIdx); mesh.TriangleIndices.Add(t1); mesh.TriangleIndices.Add(t0);
        }
        return mesh;
    }

    /// <summary>
    /// Builds a frustum (truncated cone) mesh from the axis and boundary circle samples.
    /// Separates boundary points into two rings by axis projection, computes each ring's
    /// radius analytically, then generates quads + disk caps.
    /// </summary>
    private static MeshGeometry3D BuildFrustumMesh(
        double[] axisOrigin, double[] axisDir,
        IReadOnlyList<double[]> boundaryPoints, int segments = 40)
    {
        double len = Math.Sqrt(axisDir[0]*axisDir[0] + axisDir[1]*axisDir[1] + axisDir[2]*axisDir[2]);
        if (len < 1e-10) return BuildPolygonMesh(boundaryPoints);

        double ax = axisDir[0]/len, ay = axisDir[1]/len, az = axisDir[2]/len;
        double ox = axisOrigin[0], oy = axisOrigin[1], oz = axisOrigin[2];

        if (boundaryPoints.Count < 4) return BuildPolygonMesh(boundaryPoints);

        double minT = double.MaxValue, maxT = double.MinValue;
        foreach (var p in boundaryPoints)
        {
            double t = (p[0]-ox)*ax + (p[1]-oy)*ay + (p[2]-oz)*az;
            if (t < minT) minT = t;
            if (t > maxT) maxT = t;
        }
        if (maxT - minT < 1e-10) return BuildPolygonMesh(boundaryPoints);

        double midT = (minT + maxT) / 2.0;

        static double RadDist(double[] p, double ox2, double oy2, double oz2,
                               double ax2, double ay2, double az2)
        {
            double dx = p[0]-ox2, dy = p[1]-oy2, dz = p[2]-oz2;
            double t  = dx*ax2 + dy*ay2 + dz*az2;
            double rx = dx - t*ax2, ry = dy - t*ay2, rz = dz - t*az2;
            return Math.Sqrt(rx*rx + ry*ry + rz*rz);
        }

        var botPts = boundaryPoints.Where(p => (p[0]-ox)*ax + (p[1]-oy)*ay + (p[2]-oz)*az <= midT).ToList();
        var topPts = boundaryPoints.Where(p => (p[0]-ox)*ax + (p[1]-oy)*ay + (p[2]-oz)*az >  midT).ToList();
        if (botPts.Count == 0 || topPts.Count == 0) return BuildPolygonMesh(boundaryPoints);

        double r0 = botPts.Average(p => RadDist(p, ox, oy, oz, ax, ay, az));
        double r1 = topPts.Average(p => RadDist(p, ox, oy, oz, ax, ay, az));

        double ux, uy, uz;
        if (Math.Abs(ax) < 0.9) { ux = 0; uy = az; uz = -ay; }
        else                     { ux = -az; uy = 0; uz = ax; }
        double ul = Math.Sqrt(ux*ux + uy*uy + uz*uz);
        if (ul < 1e-10) ul = 1;
        ux /= ul; uy /= ul; uz /= ul;
        double vx = ay*uz - az*uy, vy = az*ux - ax*uz, vz = ax*uy - ay*ux;

        var mesh = new MeshGeometry3D();
        for (int i = 0; i <= segments; i++)
        {
            double theta = 2 * Math.PI * i / segments;
            double cos = Math.Cos(theta), sin = Math.Sin(theta);
            double nx = cos*ux + sin*vx, ny = cos*uy + sin*vy, nz = cos*uz + sin*vz;

            mesh.Positions.Add(new Point3D(ox + nx*r0 + ax*minT,
                                           oy + ny*r0 + ay*minT,
                                           oz + nz*r0 + az*minT));
            mesh.Positions.Add(new Point3D(ox + nx*r1 + ax*maxT,
                                           oy + ny*r1 + ay*maxT,
                                           oz + nz*r1 + az*maxT));
        }
        for (int i = 0; i < segments; i++)
        {
            int b0 = i*2, t0 = b0+1, b1 = (i+1)*2, t1 = b1+1;
            mesh.TriangleIndices.Add(b0); mesh.TriangleIndices.Add(b1); mesh.TriangleIndices.Add(t0);
            mesh.TriangleIndices.Add(t0); mesh.TriangleIndices.Add(b1); mesh.TriangleIndices.Add(t1);
        }

        int botIdx = mesh.Positions.Count;
        mesh.Positions.Add(new Point3D(ox + ax*minT, oy + ay*minT, oz + az*minT));
        int topIdx = mesh.Positions.Count;
        mesh.Positions.Add(new Point3D(ox + ax*maxT, oy + ay*maxT, oz + az*maxT));
        for (int i = 0; i < segments; i++)
        {
            int b0 = i*2, b1 = (i+1)*2, t0 = i*2+1, t1 = (i+1)*2+1;
            mesh.TriangleIndices.Add(botIdx); mesh.TriangleIndices.Add(b0); mesh.TriangleIndices.Add(b1);
            mesh.TriangleIndices.Add(topIdx); mesh.TriangleIndices.Add(t1); mesh.TriangleIndices.Add(t0);
        }
        return mesh;
    }

    private static MeshGeometry3D BuildPolygonMesh(IReadOnlyList<double[]> vertices, double[]? normal = null)
    {
        var mesh = new MeshGeometry3D();
        if (vertices.Count < 3) return mesh;

        // Sort vertices by polar angle in the face plane to avoid crossing triangles.
        // Unordered vertices (common when edge vertices are deduplicated from an EDGE_LOOP)
        // cause fan-triangulation to produce diagonals that cut through the face.
        IReadOnlyList<double[]> ordered;
        if (normal is { Length: >= 3 })
        {
            double nlen = Math.Sqrt(normal[0]*normal[0] + normal[1]*normal[1] + normal[2]*normal[2]);
            if (nlen > 1e-10)
            {
                double nx = normal[0]/nlen, ny = normal[1]/nlen, nz = normal[2]/nlen;
                double cx0 = 0, cy0 = 0, cz0 = 0;
                foreach (var v in vertices) { cx0 += v[0]; cy0 += v[1]; cz0 += v[2]; }
                cx0 /= vertices.Count; cy0 /= vertices.Count; cz0 /= vertices.Count;

                // Build orthonormal 2D basis perpendicular to the face normal
                double ux, uy, uz;
                if (Math.Abs(nx) < 0.9) { ux = 0; uy = nz; uz = -ny; }
                else                     { ux = -nz; uy = 0; uz = nx; }
                double ul = Math.Sqrt(ux*ux + uy*uy + uz*uz);
                if (ul < 1e-10) ul = 1;
                ux /= ul; uy /= ul; uz /= ul;
                double vvx = ny*uz - nz*uy, vvy = nz*ux - nx*uz, vvz = nx*uy - ny*ux;

                ordered = [.. vertices.OrderBy(p =>
                    Math.Atan2(
                        (p[0]-cx0)*vvx + (p[1]-cy0)*vvy + (p[2]-cz0)*vvz,
                        (p[0]-cx0)*ux  + (p[1]-cy0)*uy  + (p[2]-cz0)*uz))];
            }
            else ordered = vertices;
        }
        else ordered = vertices;

        double cx = 0, cy = 0, cz = 0;
        foreach (var v in ordered) { cx += v[0]; cy += v[1]; cz += v[2]; }
        cx /= ordered.Count; cy /= ordered.Count; cz /= ordered.Count;

        mesh.Positions.Add(new Point3D(cx, cy, cz));
        foreach (var v in ordered)
            mesh.Positions.Add(new Point3D(v[0], v[1], v[2]));

        int n = ordered.Count;
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

    // ── Mouse orbit (window-level — works anywhere in the diff window) ────────

    private void OnPreviewMouseDown(object sender, WMouseButtonEventArgs e)
    {
        if (e.ChangedButton == WMouseButton.Left || e.ChangedButton == WMouseButton.Middle)
        {
            _lastMousePos    = e.GetPosition(this);
            _isOrbitDragging = true;
        }
    }

    private void OnPreviewMouseMove(object sender, WMouseEventArgs e)
    {
        if (!_isOrbitDragging) return;
        bool stillHeld = e.LeftButton   == WMouseButtonState.Pressed
                      || e.MiddleButton == WMouseButtonState.Pressed;
        if (!stillHeld) { _isOrbitDragging = false; return; }

        var pos = e.GetPosition(this);
        _azimuth   -= (pos.X - _lastMousePos.X) * 0.4;
        _elevation  = Math.Clamp(_elevation + (pos.Y - _lastMousePos.Y) * 0.4, -89.0, 89.0);
        _lastMousePos = pos;
        UpdateCamera();
    }

    private void OnPreviewMouseUp(object sender, WMouseButtonEventArgs e)
    {
        if (e.ChangedButton == WMouseButton.Left || e.ChangedButton == WMouseButton.Middle)
            _isOrbitDragging = false;
    }

    private void OnPreviewMouseWheel(object sender, WMouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 0.85 : 1.0 / 0.85;
        _distance = Math.Max(_distance * factor, 1e-8);
        UpdateCamera();
        e.Handled = true; // stop wheel from scrolling panels behind the viewport
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
