using System.Globalization;
using System.Text.RegularExpressions;

namespace SolidWorksPartMatcher.Infrastructure.Step;

/// <summary>
/// Minimal P21 (STEP ISO-10303-21) entity reader.
/// Builds a flat entity dictionary from the DATA section so callers can
/// resolve entity references and extract typed geometry without a full STEP parser.
///
/// Supports the AP214 subset produced by SolidWorks 2024 STEP exports.
/// </summary>
public sealed class StepP21Reader
{
    // Raw entity strings: id → text after "#NNN = " and before the terminating ";"
    private readonly Dictionary<int, string> _raw = new();

    // Detected length unit scale factor: multiply P21 lengths by this to get metres.
    public double LengthScaleToMetres { get; private set; } = 1e-3; // default: mm

    // ── Factory ────────────────────────────────────────────────────────────

    public static StepP21Reader ParseFile(string path)
    {
        var reader = new StepP21Reader();
        reader.Parse(File.ReadAllText(path));
        return reader;
    }

    // ── Low-level entity resolution ────────────────────────────────────────

    public bool TryGetRaw(int id, out string raw) => _raw.TryGetValue(id, out raw!);

    /// <summary>Returns the primary entity type name for entity <paramref name="id"/>.</summary>
    public string? GetEntityType(int id)
    {
        if (!_raw.TryGetValue(id, out var raw)) return null;
        // Compound: "( TYPE1 ( ) TYPE2 ( ) )" — return all contained type names concatenated
        // Simple:   "TYPE_NAME ( ... )"
        var m = Regex.Match(raw, @"^([A-Z][A-Z0-9_]*)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Returns all entity type names present in the raw string for an entity.
    /// Compound entities (= ( TYPE1 () TYPE2 () )) return multiple names.
    /// </summary>
    private IEnumerable<string> GetAllTypesInRaw(string raw)
        => Regex.Matches(raw, @"\b([A-Z][A-Z0-9_]*)\s*\(").Select(m => m.Groups[1].Value);

    // ── Geometry helpers ───────────────────────────────────────────────────

    /// <summary>Parses a DIRECTION entity → unit vector (NOT scaled — directions are dimensionless).</summary>
    public bool TryGetDirection(int id, out double[] xyz)
    {
        xyz = [];
        if (!_raw.TryGetValue(id, out var raw)) return false;
        var m = Regex.Match(raw, @"DIRECTION\s*\(\s*'[^']*'\s*,\s*\(\s*([^)]+)\s*\)");
        if (!m.Success) return false;
        return TryParseDoubles(m.Groups[1].Value, 3, out xyz);
    }

    /// <summary>Parses a CARTESIAN_POINT entity → position in metres (scaled by LengthScaleToMetres).</summary>
    public bool TryGetCartesianPoint(int id, out double[] xyz)
    {
        xyz = [];
        if (!_raw.TryGetValue(id, out var raw)) return false;
        var m = Regex.Match(raw, @"CARTESIAN_POINT\s*\(\s*'[^']*'\s*,\s*\(\s*([^)]+)\s*\)");
        if (!m.Success) return false;
        if (!TryParseDoubles(m.Groups[1].Value, 3, out xyz)) return false;
        for (int i = 0; i < xyz.Length; i++) xyz[i] *= LengthScaleToMetres;
        return true;
    }

    /// <summary>
    /// Parses AXIS2_PLACEMENT_3D entity.
    /// Returns false when axis or refDir are wildcards (*), indicating a degenerate placement.
    /// </summary>
    public bool TryGetAxisPlacement(int id, out double[] origin, out double[] axis, out double[] refDir)
    {
        origin = axis = refDir = [];
        if (!_raw.TryGetValue(id, out var raw)) return false;
        var m = Regex.Match(raw,
            @"AXIS2_PLACEMENT_3D\s*\(\s*'[^']*'\s*,\s*(#\d+|\*)\s*,\s*(#\d+|\*)\s*,\s*(#\d+|\*)\s*\)");
        if (!m.Success) return false;

        if (!TryResolveRef(m.Groups[1].Value, out int originId) || !TryGetCartesianPoint(originId, out origin))
            origin = [0, 0, 0];

        // axis and refDir may be * (wildcard) — use defaults in that case
        if (TryResolveRef(m.Groups[2].Value, out int axisId) && TryGetDirection(axisId, out axis))
        { /* got it */ }
        else
            axis = [0, 0, 1];

        if (TryResolveRef(m.Groups[3].Value, out int refDirId) && TryGetDirection(refDirId, out refDir))
        { /* got it */ }
        else
            refDir = [1, 0, 0];

        return true;
    }

    // ── Surface parameter extraction ───────────────────────────────────────

    /// <summary>CYLINDRICAL_SURFACE → (radius in metres, axis direction).</summary>
    public bool TryGetCylinderParams(int id, out double radiusM, out double[] axis)
    {
        radiusM = 0; axis = [];
        if (!_raw.TryGetValue(id, out var raw)) return false;
        var m = Regex.Match(raw,
            @"CYLINDRICAL_SURFACE\s*\(\s*'[^']*'\s*,\s*(#\d+)\s*,\s*([0-9Ee.+\-]+)\s*\)");
        if (!m.Success) return false;
        if (!TryResolveRef(m.Groups[1].Value, out int placeId)) return false;
        if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out radiusM)) return false;
        radiusM *= LengthScaleToMetres;
        TryGetAxisPlacement(placeId, out _, out axis, out _);
        return true;
    }

    /// <summary>CYLINDRICAL_SURFACE → (radius in metres, axis origin in metres, axis direction).</summary>
    public bool TryGetCylinderParamsFull(int id, out double radiusM, out double[] axisOrigin, out double[] axisDir)
    {
        radiusM = 0; axisOrigin = []; axisDir = [];
        if (!_raw.TryGetValue(id, out var raw)) return false;
        var m = Regex.Match(raw,
            @"CYLINDRICAL_SURFACE\s*\(\s*'[^']*'\s*,\s*(#\d+)\s*,\s*([0-9Ee.+\-]+)\s*\)");
        if (!m.Success) return false;
        if (!TryResolveRef(m.Groups[1].Value, out int placeId)) return false;
        if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out radiusM)) return false;
        radiusM *= LengthScaleToMetres;
        TryGetAxisPlacement(placeId, out axisOrigin, out axisDir, out _);
        return true;
    }

    /// <summary>PLANE → normal direction (the axis of AXIS2_PLACEMENT_3D).</summary>
    public bool TryGetPlaneNormal(int id, out double[] normal)
    {
        normal = [];
        if (!_raw.TryGetValue(id, out var raw)) return false;
        var m = Regex.Match(raw, @"PLANE\s*\(\s*'[^']*'\s*,\s*(#\d+)\s*\)");
        if (!m.Success) return false;
        if (!TryResolveRef(m.Groups[1].Value, out int placeId)) return false;
        return TryGetAxisPlacement(placeId, out _, out normal, out _);
    }

    /// <summary>PLANE → (origin in metres, normal direction).</summary>
    public bool TryGetPlaneFull(int id, out double[] origin, out double[] normal)
    {
        origin = []; normal = [];
        if (!_raw.TryGetValue(id, out var raw)) return false;
        var m = Regex.Match(raw, @"PLANE\s*\(\s*'[^']*'\s*,\s*(#\d+)\s*\)");
        if (!m.Success) return false;
        if (!TryResolveRef(m.Groups[1].Value, out int placeId)) return false;
        return TryGetAxisPlacement(placeId, out origin, out normal, out _);
    }

    /// <summary>CONICAL_SURFACE → (half-angle radians, apex-radius in metres, axis direction).</summary>
    public bool TryGetConeParams(int id, out double halfAngle, out double apexRadiusM, out double[] axis)
    {
        halfAngle = apexRadiusM = 0; axis = [];
        if (!_raw.TryGetValue(id, out var raw)) return false;
        var m = Regex.Match(raw,
            @"CONICAL_SURFACE\s*\(\s*'[^']*'\s*,\s*(#\d+)\s*,\s*([0-9Ee.+\-]+)\s*,\s*([0-9Ee.+\-]+)\s*\)");
        if (!m.Success) return false;
        if (!TryResolveRef(m.Groups[1].Value, out int placeId)) return false;
        double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double r);
        double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ha);
        apexRadiusM = r * LengthScaleToMetres;
        halfAngle   = ha; // radians — no scale
        TryGetAxisPlacement(placeId, out _, out axis, out _);
        return true;
    }

    /// <summary>CONICAL_SURFACE → (half-angle, apex-radius, axis origin, axis direction).</summary>
    public bool TryGetConeParamsFull(int id, out double halfAngle, out double apexRadiusM,
        out double[] axisOrigin, out double[] axisDir)
    {
        halfAngle = apexRadiusM = 0; axisOrigin = axisDir = [];
        if (!_raw.TryGetValue(id, out var raw)) return false;
        var m = Regex.Match(raw,
            @"CONICAL_SURFACE\s*\(\s*'[^']*'\s*,\s*(#\d+)\s*,\s*([0-9Ee.+\-]+)\s*,\s*([0-9Ee.+\-]+)\s*\)");
        if (!m.Success) return false;
        if (!TryResolveRef(m.Groups[1].Value, out int placeId)) return false;
        double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double r);
        double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ha);
        apexRadiusM = r * LengthScaleToMetres;
        halfAngle   = ha;
        TryGetAxisPlacement(placeId, out axisOrigin, out axisDir, out _);
        return true;
    }

    /// <summary>SPHERICAL_SURFACE → radius in metres.</summary>
    public bool TryGetSphereRadius(int id, out double radiusM)
    {
        radiusM = 0;
        if (!_raw.TryGetValue(id, out var raw)) return false;
        var m = Regex.Match(raw,
            @"SPHERICAL_SURFACE\s*\(\s*'[^']*'\s*,\s*#\d+\s*,\s*([0-9Ee.+\-]+)\s*\)");
        if (!m.Success) return false;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out radiusM)) return false;
        radiusM *= LengthScaleToMetres;
        return true;
    }

    /// <summary>TOROIDAL_SURFACE → (major radius in metres, minor radius in metres).</summary>
    public bool TryGetTorusParams(int id, out double majorR, out double minorR)
    {
        majorR = minorR = 0;
        if (!_raw.TryGetValue(id, out var raw)) return false;
        var m = Regex.Match(raw,
            @"TOROIDAL_SURFACE\s*\(\s*'[^']*'\s*,\s*#\d+\s*,\s*([0-9Ee.+\-]+)\s*,\s*([0-9Ee.+\-]+)\s*\)");
        if (!m.Success) return false;
        double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out majorR);
        double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out minorR);
        majorR *= LengthScaleToMetres;
        minorR *= LengthScaleToMetres;
        return true;
    }

    // ── Collections ────────────────────────────────────────────────────────

    /// <summary>Returns (surfaceEntityId, orientation) for every ADVANCED_FACE in the file.</summary>
    public IReadOnlyList<(int SurfaceId, bool Outward)> GetAdvancedFaces()
    {
        var faces = new List<(int, bool)>();
        foreach (var (_, raw) in _raw)
        {
            // ADVANCED_FACE ( 'name', ( bounds... ), surface_ref, .T. )
            var m = Regex.Match(raw,
                @"ADVANCED_FACE\s*\(\s*'[^']*'\s*,\s*\([^)]*\)\s*,\s*(#\d+)\s*,\s*\.(T|F)\.\s*\)");
            if (!m.Success) continue;
            if (!TryResolveRef(m.Groups[1].Value, out int surfId)) continue;
            faces.Add((surfId, m.Groups[2].Value == "T"));
        }
        return faces;
    }

    /// <summary>Returns all CARTESIAN_POINT coordinates (in metres) in the file.</summary>
    public IReadOnlyList<double[]> GetAllCartesianPoints()
    {
        var pts = new List<double[]>();
        foreach (var (id, _) in _raw)
        {
            if (TryGetCartesianPoint(id, out var xyz))
                pts.Add(xyz);
        }
        return pts;
    }

    /// <summary>Count of MANIFOLD_SOLID_BREP entities — approximates SolidBodyCount.</summary>
    public int GetManifoldSolidCount()
        => _raw.Values.Count(raw => raw.Contains("MANIFOLD_SOLID_BREP"));

    /// <summary>
    /// Returns a <see cref="StepFaceGeometry"/> for every ADVANCED_FACE in the file.
    /// Each record carries the surface type, canonical descriptor, boundary vertex positions,
    /// and surface-specific parameters (axis, radius) ready for 3D rendering or diffing.
    /// </summary>
    public IReadOnlyList<StepFaceGeometry> GetFaceGeometries()
    {
        var result = new List<StepFaceGeometry>();
        var afRx = new Regex(
            @"ADVANCED_FACE\s*\(\s*'[^']*'\s*,\s*\(([^)]*)\)\s*,\s*(#\d+)\s*,\s*\.(T|F)\.\s*\)",
            RegexOptions.Compiled);

        foreach (var (_, raw) in _raw)
        {
            var m = afRx.Match(raw);
            if (!m.Success) continue;

            var boundRefs = ParseRefList(m.Groups[1].Value);
            if (!TryResolveRef(m.Groups[2].Value, out int surfId)) continue;
            string surfType = GetEntityType(surfId) ?? "UNKNOWN";

            var boundaryPoints = CollectBoundaryPoints(boundRefs);
            BuildSurfaceParams(surfId, surfType,
                out string descriptor, out double[]? axisOrigin,
                out double[]? axisDir, out double? radius);

            result.Add(new StepFaceGeometry(
                SurfaceId:      surfId,
                SurfaceType:    surfType,
                Descriptor:     descriptor,
                BoundaryPoints: boundaryPoints,
                AxisOrigin:     axisOrigin,
                AxisDirection:  axisDir,
                Radius:         radius));
        }

        return result;
    }

    // ── GetFaceGeometries helpers ──────────────────────────────────────────

    private List<double[]> CollectBoundaryPoints(IReadOnlyList<int> boundRefs)
    {
        var seen        = new HashSet<int>();
        var seenCircles = new HashSet<int>();
        var pts         = new List<double[]>();
        // Only outer bounds — inner bounds (holes) cause fan-triangulation artifacts
        var faceBindRx  = new Regex(@"FACE_OUTER_BOUND\s*\(\s*'[^']*'\s*,\s*(#\d+)\s*,", RegexOptions.Compiled);
        var edgeLoopRx  = new Regex(@"EDGE_LOOP\s*\(\s*'[^']*'\s*,\s*\(\s*((?:#\d+\s*,?\s*)+)\)", RegexOptions.Compiled);
        var orientRx    = new Regex(@"ORIENTED_EDGE\s*\(\s*'[^']*'\s*,\s*\*\s*,\s*\*\s*,\s*(#\d+)\s*,", RegexOptions.Compiled);
        // Capture group 3 = curve geometry ref (LINE, CIRCLE, B_SPLINE…)
        var edgeCurveRx = new Regex(@"EDGE_CURVE\s*\(\s*'[^']*'\s*,\s*(#\d+)\s*,\s*(#\d+)\s*,\s*(#\d+)\s*,", RegexOptions.Compiled);
        var vertexRx    = new Regex(@"VERTEX_POINT\s*\(\s*'[^']*'\s*,\s*(#\d+)\s*\)", RegexOptions.Compiled);
        var circleRx    = new Regex(@"CIRCLE\s*\(\s*'[^']*'\s*,\s*(#\d+)\s*,\s*([0-9Ee.+\-]+)\s*\)", RegexOptions.Compiled);

        foreach (var boundId in boundRefs)
        {
            if (!_raw.TryGetValue(boundId, out var boundRaw)) continue;
            var fbm = faceBindRx.Match(boundRaw);
            if (!fbm.Success) continue;
            if (!TryResolveRef(fbm.Groups[1].Value, out int loopId)) continue;
            if (!_raw.TryGetValue(loopId, out var loopRaw)) continue;

            var elm = edgeLoopRx.Match(loopRaw);
            if (!elm.Success) continue;
            var orientedEdgeRefs = ParseRefList(elm.Groups[1].Value);

            foreach (var oeId in orientedEdgeRefs)
            {
                if (!_raw.TryGetValue(oeId, out var oeRaw)) continue;
                var oem = orientRx.Match(oeRaw);
                if (!oem.Success) continue;
                if (!TryResolveRef(oem.Groups[1].Value, out int ecId)) continue;
                if (!_raw.TryGetValue(ecId, out var ecRaw)) continue;

                var ecm = edgeCurveRx.Match(ecRaw);
                if (!ecm.Success) continue;

                // Corner vertices (edge endpoints)
                foreach (var vRefStr in new[] { ecm.Groups[1].Value, ecm.Groups[2].Value })
                {
                    if (!TryResolveRef(vRefStr, out int vertId)) continue;
                    if (!seen.Add(vertId)) continue;
                    if (!_raw.TryGetValue(vertId, out var vertRaw)) continue;
                    var vm = vertexRx.Match(vertRaw);
                    if (!vm.Success) continue;
                    if (!TryResolveRef(vm.Groups[1].Value, out int cpId)) continue;
                    if (TryGetCartesianPoint(cpId, out var xyz))
                        pts.Add(xyz);
                }

                // CIRCLE edges: generate 32 sample points around the circumference.
                // This gives circular faces (caps, flanges) enough vertices to render
                // as proper disks rather than degenerate single-point geometry.
                if (!TryResolveRef(ecm.Groups[3].Value, out int curveId)) continue;
                if (!_raw.TryGetValue(curveId, out var curveRaw)) continue;
                if (!seenCircles.Add(curveId)) continue; // one set of samples per unique CIRCLE entity

                var cm = circleRx.Match(curveRaw);
                if (!cm.Success) continue;
                if (!TryResolveRef(cm.Groups[1].Value, out int placeId)) continue;
                if (!double.TryParse(cm.Groups[2].Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double circR)) continue;
                circR *= LengthScaleToMetres;
                if (!TryGetAxisPlacement(placeId, out var cen, out var coneAx, out var refDir)) continue;

                // tangent = axisDir × refDir  (builds 2D basis in the circle plane)
                double tx = coneAx[1]*refDir[2] - coneAx[2]*refDir[1];
                double ty = coneAx[2]*refDir[0] - coneAx[0]*refDir[2];
                double tz = coneAx[0]*refDir[1] - coneAx[1]*refDir[0];
                double tlen = Math.Sqrt(tx*tx + ty*ty + tz*tz);
                if (tlen < 1e-10) continue;
                tx /= tlen; ty /= tlen; tz /= tlen;

                const int CircleSamples = 32;
                for (int i = 0; i < CircleSamples; i++)
                {
                    double theta = 2 * Math.PI * i / CircleSamples;
                    double c = Math.Cos(theta), s = Math.Sin(theta);
                    pts.Add(new[]
                    {
                        cen[0] + (refDir[0]*c + tx*s) * circR,
                        cen[1] + (refDir[1]*c + ty*s) * circR,
                        cen[2] + (refDir[2]*c + tz*s) * circR
                    });
                }
            }
        }

        return pts;
    }

    private void BuildSurfaceParams(
        int surfId, string surfType,
        out string descriptor, out double[]? axisOrigin,
        out double[]? axisDir, out double? radius)
    {
        axisOrigin = null; axisDir = null; radius = null;

        switch (surfType)
        {
            case "CYLINDRICAL_SURFACE":
                if (TryGetCylinderParamsFull(surfId, out double r, out var orig, out var ax))
                {
                    var axCopy = (double[])ax.Clone();
                    CanonicalizeAxis(axCopy);
                    descriptor = $"CYLINDER|{r:R}|{axCopy[0]:F4}|{axCopy[1]:F4}|{axCopy[2]:F4}";
                    axisOrigin = orig; axisDir = ax; radius = r;
                }
                else descriptor = "CYLINDER|PARSE_ERROR";
                break;

            case "PLANE":
                if (TryGetPlaneFull(surfId, out var planeOrig, out var planeNorm))
                {
                    var nCopy = (double[])planeNorm.Clone();
                    CanonicalizeAxis(nCopy);
                    descriptor = $"PLANE|{nCopy[0]:F4}|{nCopy[1]:F4}|{nCopy[2]:F4}";
                    axisOrigin = planeOrig; axisDir = planeNorm;
                }
                else descriptor = "PLANE|PARSE_ERROR";
                break;

            case "CONICAL_SURFACE":
                if (TryGetConeParamsFull(surfId, out double ha, out double ra, out var coneOrig, out var coneAx))
                {
                    var axCopy = (double[])coneAx.Clone();
                    CanonicalizeAxis(axCopy);
                    descriptor = $"CONE|{ha:F6}|{ra:R}|{axCopy[0]:F4}|{axCopy[1]:F4}|{axCopy[2]:F4}";
                    axisOrigin = coneOrig; axisDir = coneAx; radius = ra;
                }
                else descriptor = "CONE|PARSE_ERROR";
                break;

            case "SPHERICAL_SURFACE":
                descriptor = TryGetSphereRadius(surfId, out double rs) ? $"SPHERE|{rs:R}" : "SPHERE|PARSE_ERROR";
                if (TryGetSphereRadius(surfId, out double rs2)) radius = rs2;
                break;

            case "TOROIDAL_SURFACE":
                descriptor = TryGetTorusParams(surfId, out double majorR, out double minorR)
                    ? $"TORUS|{majorR:R}|{minorR:R}" : "TORUS|PARSE_ERROR";
                break;

            default:
                descriptor = $"OTHER|{surfType}";
                break;
        }
    }

    private static IReadOnlyList<int> ParseRefList(string csv)
    {
        var result = new List<int>();
        foreach (Match m in Regex.Matches(csv, @"#(\d+)"))
            if (int.TryParse(m.Groups[1].Value, out int id))
                result.Add(id);
        return result;
    }

    private static void CanonicalizeAxis(double[] axis)
    {
        if (axis.Length < 3) return;
        int dom = 0;
        for (int i = 1; i < 3; i++)
            if (Math.Abs(axis[i]) > Math.Abs(axis[dom])) dom = i;
        if (axis[dom] < 0)
            for (int i = 0; i < axis.Length; i++) axis[i] = -axis[i];
        for (int i = 0; i < axis.Length; i++)
            if (Math.Abs(axis[i]) < 1e-9) axis[i] = 0.0;
    }

    // ── Private implementation ─────────────────────────────────────────────

    private void Parse(string text)
    {
        // Collapse multi-line entity continuations into single lines.
        // P21 entities end with ' ;' — any line not ending with ';' continues.
        var lines = new List<string>();
        var buf = new System.Text.StringBuilder();
        bool inData = false;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line == "DATA;") { inData = true; continue; }
            if (!inData) continue;
            if (line == "ENDSEC;") break;
            if (string.IsNullOrEmpty(line)) continue;

            buf.Append(line);
            if (line.EndsWith(';'))
            {
                lines.Add(buf.ToString());
                buf.Clear();
            }
        }

        // Parse each entity: #NNN = rest ;
        var entityRx = new Regex(@"^#(\d+)\s*=\s*(.+?)\s*;\s*$", RegexOptions.Singleline);
        foreach (var line in lines)
        {
            var m = entityRx.Match(line);
            if (!m.Success) continue;
            int id = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            _raw[id] = m.Groups[2].Value.Trim();
        }

        DetectLengthUnit();
    }

    private void DetectLengthUnit()
    {
        // Find entities containing both LENGTH_UNIT and SI_UNIT to determine length scale.
        foreach (var (_, raw) in _raw)
        {
            if (!raw.Contains("LENGTH_UNIT")) continue;
            if (raw.Contains(".MILLI., .METRE."))   { LengthScaleToMetres = 1e-3;   return; }
            if (raw.Contains(".CENTI., .METRE."))   { LengthScaleToMetres = 1e-2;   return; }
            if (raw.Contains(".METRE."))             { LengthScaleToMetres = 1.0;    return; }
            if (raw.Contains(".INCH."))              { LengthScaleToMetres = 0.0254; return; }
        }
        // Default: millimetres (most common for SolidWorks exports)
        LengthScaleToMetres = 1e-3;
    }

    // Parses "#NNN" → int id
    private static bool TryResolveRef(string token, out int id)
    {
        id = 0;
        var m = Regex.Match(token.Trim(), @"^#(\d+)$");
        if (!m.Success) return false;
        return int.TryParse(m.Groups[1].Value, out id);
    }

    // Parses comma-separated doubles from a raw token string
    private static bool TryParseDoubles(string csv, int expectedCount, out double[] values)
    {
        values = [];
        var parts = csv.Split(',');
        if (parts.Length < expectedCount) return false;
        var result = new double[expectedCount];
        for (int i = 0; i < expectedCount; i++)
        {
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                return false;
        }
        values = result;
        return true;
    }
}
