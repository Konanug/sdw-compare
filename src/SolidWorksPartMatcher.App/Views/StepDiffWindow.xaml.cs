using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using SolidWorksPartMatcher.Infrastructure.Step;
using WpfColor               = System.Windows.Media.Color;
using WpfMouseEventArgs      = System.Windows.Input.MouseEventArgs;
using WpfPoint               = System.Windows.Point;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using WpfMouseButtonState    = System.Windows.Input.MouseButtonState;
using WpfMouseWheelEventArgs  = System.Windows.Input.MouseWheelEventArgs;
using WpfMessageBox          = System.Windows.MessageBox;
using WpfMessageBoxButton    = System.Windows.MessageBoxButton;
using WpfMessageBoxImage     = System.Windows.MessageBoxImage;

namespace SolidWorksPartMatcher.App.Views;

/// <summary>
/// Side-by-side 3D diff window for two STEP files.
/// Faces are colour-coded: green = matching, orange-red = A-only / different,
/// blue = B-only, grey = complex/unrecognised surface.
/// Code-behind only — no MVVM.
/// </summary>
public partial class StepDiffWindow : Window
{
    // ── Colour palette ─────────────────────────────────────────────────────
    private static readonly WpfColor ColourMatch   = WpfColor.FromRgb(0x4C, 0xAF, 0x50); // green
    private static readonly WpfColor ColourAOnly   = WpfColor.FromRgb(0xFF, 0x6B, 0x35); // orange-red
    private static readonly WpfColor ColourBOnly   = WpfColor.FromRgb(0x21, 0x96, 0xF3); // blue
    private static readonly WpfColor ColourComplex = WpfColor.FromRgb(0x9E, 0x9E, 0x9E); // grey

    // ── Camera orbit state ─────────────────────────────────────────────────
    private double   _azimuth   = 30.0;  // degrees
    private double   _elevation = 20.0;  // degrees
    private double   _distance  = 1.0;
    private Point3D  _center    = new(0, 0, 0);
    private WpfPoint _dragStart;
    private bool     _isDragging;

    public StepDiffWindow(string displayName, string pathA, string pathB)
    {
        InitializeComponent();

        TitleLabel.Text = $"{Path.GetFileName(pathA)}  vs  {Path.GetFileName(pathB)}  — {displayName}";
        LabelA.Text = $"Part A: {Path.GetFileName(pathA)}";
        LabelB.Text = $"Part B: {Path.GetFileName(pathB)}";

        Loaded += (_, _) => LoadAndDiff(pathA, pathB);
    }

    // ── Load, diff and render ──────────────────────────────────────────────

    private void LoadAndDiff(string pathA, string pathB)
    {
        IReadOnlyList<StepFaceGeometry> facesA;
        IReadOnlyList<StepFaceGeometry> facesB;

        try
        {
            var readerA = StepP21Reader.ParseFile(pathA);
            var readerB = StepP21Reader.ParseFile(pathB);
            facesA = readerA.GetFaceGeometries();
            facesB = readerB.GetFaceGeometries();
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(
                $"Failed to parse STEP files:\n{ex.Message}",
                "Parse Error", WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);
            return;
        }

        // Build descriptor sets for match classification
        var descB = new HashSet<string>(facesB.Select(f => f.Descriptor), StringComparer.Ordinal);
        var descA = new HashSet<string>(facesA.Select(f => f.Descriptor), StringComparer.Ordinal);

        // Build 3D models
        var groupA = new Model3DGroup();
        var groupB = new Model3DGroup();

        int matchCount = 0, aOnlyCount = 0, bOnlyCount = 0;

        // Add lights
        groupA.Children.Add(new DirectionalLight(WpfColor.FromRgb(0xCC, 0xCC, 0xCC), new Vector3D(-1, -2, -1)));
        groupA.Children.Add(new AmbientLight(WpfColor.FromRgb(0x55, 0x55, 0x55)));
        groupB.Children.Add(new DirectionalLight(WpfColor.FromRgb(0xCC, 0xCC, 0xCC), new Vector3D(-1, -2, -1)));
        groupB.Children.Add(new AmbientLight(WpfColor.FromRgb(0x55, 0x55, 0x55)));

        foreach (var face in facesA)
        {
            WpfColor colour;
            if (face.Descriptor.StartsWith("OTHER|", StringComparison.Ordinal))
            {
                colour = ColourComplex;
            }
            else if (descB.Contains(face.Descriptor))
            {
                colour = ColourMatch;
                matchCount++;
            }
            else
            {
                colour = ColourAOnly;
                aOnlyCount++;
            }

            var mesh = BuildFaceMesh(face);
            if (mesh != null)
            {
                var model = MakeModel(mesh, colour);
                if (model != null) groupA.Children.Add(model);
            }
        }

        foreach (var face in facesB)
        {
            WpfColor colour;
            if (face.Descriptor.StartsWith("OTHER|", StringComparison.Ordinal))
            {
                colour = ColourComplex;
            }
            else if (descA.Contains(face.Descriptor))
            {
                colour = ColourMatch;
            }
            else
            {
                colour = ColourBOnly;
                bOnlyCount++;
            }

            var mesh = BuildFaceMesh(face);
            if (mesh != null)
            {
                var model = MakeModel(mesh, colour);
                if (model != null) groupB.Children.Add(model);
            }
        }

        ModelA.Content = groupA;
        ModelB.Content = groupB;

        // Compute shared bounds from all boundary-point data
        ComputeCenter(facesA, facesB);
        UpdateCameras();

        SummaryLabel.Text =
            $"A: {facesA.Count} faces   " +
            $"B: {facesB.Count} faces   " +
            $"Matching: {matchCount}   " +
            $"Different: {aOnlyCount + bOnlyCount}";
    }

    private static GeometryModel3D? MakeModel(MeshGeometry3D mesh, WpfColor colour)
    {
        if (mesh.Positions.Count == 0) return null;
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        var mat = new DiffuseMaterial(brush);
        mat.Freeze();
        return new GeometryModel3D(mesh, mat);
    }

    // ── Mesh building ──────────────────────────────────────────────────────

    private static MeshGeometry3D? BuildFaceMesh(StepFaceGeometry face)
    {
        return face.SurfaceType switch
        {
            "CYLINDRICAL_SURFACE" when face.AxisOrigin != null && face.AxisDirection != null && face.Radius != null
                => BuildCylinderMesh(face.AxisOrigin, face.AxisDirection, face.Radius.Value, face.BoundaryPoints),
            _   => BuildPolygonMesh(face.BoundaryPoints)
        };
    }

    /// <summary>
    /// Build a cylinder side-surface mesh from axis params and boundary vertices.
    /// Projects boundary points onto the axis to determine height extent.
    /// </summary>
    private static MeshGeometry3D BuildCylinderMesh(
        double[] axisOrigin, double[] axisDir, double radius,
        IReadOnlyList<double[]> boundaryPoints,
        int segments = 32)
    {
        // Normalise axis direction
        double len = Math.Sqrt(axisDir[0] * axisDir[0] + axisDir[1] * axisDir[1] + axisDir[2] * axisDir[2]);
        if (len < 1e-10) return BuildPolygonMesh(boundaryPoints);

        double ax = axisDir[0] / len, ay = axisDir[1] / len, az = axisDir[2] / len;
        double ox = axisOrigin[0], oy = axisOrigin[1], oz = axisOrigin[2];

        // Project boundary points onto axis to find height extent
        double minT = double.MaxValue, maxT = double.MinValue;
        foreach (var p in boundaryPoints)
        {
            double dx = p[0] - ox, dy = p[1] - oy, dz = p[2] - oz;
            double t  = dx * ax + dy * ay + dz * az;
            if (t < minT) minT = t;
            if (t > maxT) maxT = t;
        }

        if (maxT - minT < 1e-10)
        {
            // degenerate: use radius as a nominal height
            minT = -radius;
            maxT =  radius;
        }

        // Build orthonormal basis perpendicular to axis
        double ux, uy, uz;
        if (Math.Abs(ax) < 0.9)
        { ux = 0; uy = az; uz = -ay; }
        else
        { ux = -az; uy = 0; uz = ax; }
        double ulen = Math.Sqrt(ux * ux + uy * uy + uz * uz);
        if (ulen < 1e-10) ulen = 1;
        ux /= ulen; uy /= ulen; uz /= ulen;

        // v = axis × u
        double vx = ay * uz - az * uy;
        double vy = az * ux - ax * uz;
        double vz = ax * uy - ay * ux;

        var mesh = new MeshGeometry3D();

        for (int i = 0; i <= segments; i++)
        {
            double theta = 2 * Math.PI * i / segments;
            double cos   = Math.Cos(theta);
            double sin   = Math.Sin(theta);
            double nx = cos * ux + sin * vx;
            double ny = cos * uy + sin * vy;
            double nz = cos * uz + sin * vz;

            // Bottom vertex
            mesh.Positions.Add(new Point3D(
                ox + nx * radius + ax * minT,
                oy + ny * radius + ay * minT,
                oz + nz * radius + az * minT));

            // Top vertex
            mesh.Positions.Add(new Point3D(
                ox + nx * radius + ax * maxT,
                oy + ny * radius + ay * maxT,
                oz + nz * radius + az * maxT));
        }

        // Side quads → two triangles each
        for (int i = 0; i < segments; i++)
        {
            int b0 = i * 2;
            int t0 = b0 + 1;
            int b1 = (i + 1) * 2;
            int t1 = b1 + 1;

            mesh.TriangleIndices.Add(b0); mesh.TriangleIndices.Add(b1); mesh.TriangleIndices.Add(t0);
            mesh.TriangleIndices.Add(t0); mesh.TriangleIndices.Add(b1); mesh.TriangleIndices.Add(t1);
        }

        return mesh;
    }

    /// <summary>
    /// Fan-triangulate a polygon from boundary vertices.
    /// Uses centroid as the fan origin.
    /// </summary>
    private static MeshGeometry3D BuildPolygonMesh(IReadOnlyList<double[]> vertices)
    {
        var mesh = new MeshGeometry3D();
        if (vertices.Count < 3) return mesh;

        // Centroid
        double cx = 0, cy = 0, cz = 0;
        foreach (var v in vertices) { cx += v[0]; cy += v[1]; cz += v[2]; }
        cx /= vertices.Count; cy /= vertices.Count; cz /= vertices.Count;

        mesh.Positions.Add(new Point3D(cx, cy, cz)); // index 0 = centroid

        foreach (var v in vertices)
            mesh.Positions.Add(new Point3D(v[0], v[1], v[2]));

        int n = vertices.Count;
        for (int i = 0; i < n; i++)
        {
            int curr = i + 1;
            int next = (i + 1) % n + 1;
            mesh.TriangleIndices.Add(0);
            mesh.TriangleIndices.Add(curr);
            mesh.TriangleIndices.Add(next);
        }

        return mesh;
    }

    // ── Camera control ─────────────────────────────────────────────────────

    private void ComputeCenter(
        IReadOnlyList<StepFaceGeometry> facesA,
        IReadOnlyList<StepFaceGeometry> facesB)
    {
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;

        static void Expand(
            IReadOnlyList<double[]> pts,
            ref double mnX, ref double mxX,
            ref double mnY, ref double mxY,
            ref double mnZ, ref double mxZ)
        {
            foreach (var p in pts)
            {
                if (p.Length < 3) continue;
                if (p[0] < mnX) mnX = p[0]; if (p[0] > mxX) mxX = p[0];
                if (p[1] < mnY) mnY = p[1]; if (p[1] > mxY) mxY = p[1];
                if (p[2] < mnZ) mnZ = p[2]; if (p[2] > mxZ) mxZ = p[2];
            }
        }

        foreach (var f in facesA) Expand(f.BoundaryPoints, ref minX, ref maxX, ref minY, ref maxY, ref minZ, ref maxZ);
        foreach (var f in facesB) Expand(f.BoundaryPoints, ref minX, ref maxX, ref minY, ref maxY, ref minZ, ref maxZ);

        if (minX > maxX) { _center = new Point3D(0, 0, 0); _distance = 0.5; return; }

        _center = new Point3D(
            (minX + maxX) / 2,
            (minY + maxY) / 2,
            (minZ + maxZ) / 2);

        double span = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
        _distance = span * 1.8;
        if (_distance < 1e-6) _distance = 0.5;
    }

    private void UpdateCameras()
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

        var look = new Vector3D(
            _center.X - pos.X,
            _center.Y - pos.Y,
            _center.Z - pos.Z);

        var up = new Vector3D(0, 1, 0);

        CameraA.Position      = pos;
        CameraA.LookDirection = look;
        CameraA.UpDirection   = up;

        CameraB.Position      = pos;
        CameraB.LookDirection = look;
        CameraB.UpDirection   = up;
    }

    // ── Mouse event handlers ───────────────────────────────────────────────

    private void Viewport_MouseLeftButtonDown(object sender, WpfMouseButtonEventArgs e)
    {
        if (sender is not UIElement el) return;
        _dragStart   = e.GetPosition(el);
        _isDragging  = true;
        el.CaptureMouse();
    }

    private void Viewport_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != WpfMouseButtonState.Pressed) return;
        if (sender is not UIElement el) return;

        var pos    = e.GetPosition(el);
        double dx2 = pos.X - _dragStart.X;
        double dy2 = pos.Y - _dragStart.Y;
        _dragStart = pos;

        _azimuth   -= dx2 * 0.4;
        _elevation  = Math.Clamp(_elevation + dy2 * 0.4, -89.0, 89.0);

        UpdateCameras();
    }

    private void Viewport_MouseLeftButtonUp(object sender, WpfMouseButtonEventArgs e)
    {
        _isDragging = false;
        if (sender is UIElement el) el.ReleaseMouseCapture();
    }

    private void Viewport_MouseWheel(object sender, WpfMouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 0.85 : 1.0 / 0.85;
        _distance = Math.Max(_distance * factor, 1e-6);
        UpdateCameras();
    }

    // ── Buttons ────────────────────────────────────────────────────────────

    private void ResetView_Click(object sender, RoutedEventArgs e)
    {
        _azimuth   = 30.0;
        _elevation = 20.0;
        UpdateCameras();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
