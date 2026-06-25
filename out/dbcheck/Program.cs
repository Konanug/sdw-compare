using ClosedXML.Excel;
using Microsoft.Data.Sqlite;

var dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "SolidWorksPartMatcher", "partmatcher.db");

if (!File.Exists(dbPath)) { Console.Error.WriteLine($"DB not found: {dbPath}"); return 1; }

using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
conn.Open();

var runId = Query(conn,
    "SELECT id FROM scan_runs WHERE status='Completed' ORDER BY started_utc DESC LIMIT 1",
    r => r.GetString(0)).FirstOrDefault();
if (runId == null) { Console.Error.WriteLine("No completed scan run found."); return 1; }

Console.WriteLine($"Exporting run: {runId}");

var files = Query(conn,
    "SELECT id, file_name, normalized_path, sha256, status, error FROM scanned_files WHERE scan_run_id=@r",
    r => new { Id = r.GetString(0), Name = r.GetString(1), Path = r.GetString(2),
                Sha = r.IsDBNull(3) ? "" : r.GetString(3),
                Status = r.GetString(4), Error = r.IsDBNull(5) ? "" : r.GetString(5) },
    ("@r", runId));

var fps = Query(conn,
    "SELECT id, scanned_file_id, config_name, solid_body_count, surface_body_count, " +
    "sorted_bounding_box_m, volume_m3, surface_area_m2, mass_kg, face_count, edge_count, vertex_count, " +
    "feature_count, material, extractor_version_label, file_sha256 " +
    "FROM fingerprints WHERE scanned_file_id IN (SELECT id FROM scanned_files WHERE scan_run_id=@r)",
    r => new {
        Id = r.GetString(0), FileId = r.GetString(1), Config = r.GetString(2),
        SolidBodies = r.GetInt32(3), SheetBodies = r.GetInt32(4),
        BBJson = r.GetString(5),
        Volume = r.GetDouble(6), SurfaceArea = r.GetDouble(7),
        Mass = r.IsDBNull(8) ? (double?)null : r.GetDouble(8),
        Faces = r.GetInt32(9), Edges = r.GetInt32(10), Verts = r.GetInt32(11),
        Features = r.GetInt32(12),
        Material = r.IsDBNull(13) ? "" : r.GetString(13),
        ExtractorLabel = r.GetString(14),
        Sha256 = r.GetString(15)
    }, ("@r", runId));

var fpByFileId = fps.GroupBy(f => f.FileId).ToDictionary(g => g.Key, g => g.First());

var clusters = Query(conn,
    "SELECT id, canonical_name, classification, representative_fingerprint_id, review_status, reviewer_note " +
    "FROM clusters WHERE scan_run_id=@r ORDER BY canonical_name",
    r => new { Id = r.GetString(0), Name = r.GetString(1), Classification = r.GetString(2),
                RepId = r.GetString(3), ReviewStatus = r.GetString(4),
                Note = r.IsDBNull(5) ? "" : r.GetString(5) },
    ("@r", runId));

var members = Query(conn,
    "SELECT cluster_id, fingerprint_id, is_representative FROM cluster_members " +
    "WHERE cluster_id IN (SELECT id FROM clusters WHERE scan_run_id=@r)",
    r => new { ClusterId = r.GetString(0), FpId = r.GetString(1), IsRep = r.GetInt32(2) == 1 },
    ("@r", runId));

var membersByCluster = members.GroupBy(m => m.ClusterId).ToDictionary(g => g.Key, g => g.ToList());
var clusterByFpId = members.ToDictionary(m => m.FpId, m => m.ClusterId);

var pairs = Query(conn,
    "SELECT fingerprint_a_id, fingerprint_b_id, coarse_score, classification, classification_reason, " +
    "comparator_version, tolerance_profile, confidence FROM candidate_pairs WHERE scan_run_id=@r",
    r => new { FpAId = r.GetString(0), FpBId = r.GetString(1), Score = r.GetDouble(2),
                Classification = r.GetString(3), Reason = r.IsDBNull(4) ? "" : r.GetString(4),
                CompVer = r.IsDBNull(5) ? "" : r.GetString(5),
                TolProfile = r.IsDBNull(6) ? "" : r.GetString(6),
                Confidence = r.IsDBNull(7) ? (double?)null : r.GetDouble(7) },
    ("@r", runId));

var runMeta = Query(conn,
    "SELECT id, started_utc, ended_utc, source_roots, app_version, status FROM scan_runs WHERE id=@r",
    r => new { Id = r.GetString(0), Started = r.GetString(1),
                Ended = r.IsDBNull(2) ? "" : r.GetString(2),
                Roots = r.GetString(3), AppVersion = r.GetString(4), Status = r.GetString(5) },
    ("@r", runId)).First();

// â”€â”€ Excel workbook â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

using var wb = new XLWorkbook();

// PartGroups
{
    var ws = wb.Worksheets.Add("PartGroups");
    string[] h = { "Group ID", "Canonical Name", "Classification", "File Count",
                   "Review Status", "Representative Fingerprint ID", "Notes" };
    WriteHeaders(ws, h);
    int row = 2;
    foreach (var c in clusters)
    {
        int fc = membersByCluster.TryGetValue(c.Id, out var ml) ? ml.Count : 1;
        ws.Cell(row, 1).Value = c.Id;
        ws.Cell(row, 2).Value = c.Name;
        ws.Cell(row, 3).Value = c.Classification;
        ws.Cell(row, 4).Value = fc;
        ws.Cell(row, 5).Value = c.ReviewStatus;
        ws.Cell(row, 6).Value = c.RepId;
        ws.Cell(row, 7).Value = c.Note;
        if (c.Classification == "BinaryDuplicate")
            ws.Row(row).Style.Fill.BackgroundColor = XLColor.LightGreen;
        row++;
    }
    FormatTable(ws, "PartGroups", 1, row - 1, h.Length);
}

// SourceFiles
{
    var ws = wb.Worksheets.Add("SourceFiles");
    string[] h = { "Group ID", "Canonical Name", "Filename", "Full Path", "Configuration",
                   "SHA-256", "Material",
                   "BB L (mm)", "BB W (mm)", "BB H (mm)",
                   "Volume (cmÂ³)", "Surface Area (cmÂ²)", "Mass (g)",
                   "Solid Bodies", "Faces", "Edges", "Vertices", "Features",
                   "Extractor", "Scan Status" };
    WriteHeaders(ws, h);
    var fileById = files.ToDictionary(f => f.Id);
    int row = 2;
    foreach (var f in files.OrderBy(f => f.Name))
    {
        fpByFileId.TryGetValue(f.Id, out var fp);
        clusterByFpId.TryGetValue(fp?.Id ?? "", out var cid);
        var cl = clusters.FirstOrDefault(c => c.Id == cid);
        double[] bb = ParseBB(fp?.BBJson);
        ws.Cell(row, 1).Value = cid ?? "";
        ws.Cell(row, 2).Value = cl?.Name ?? "";
        ws.Cell(row, 3).Value = f.Name;
        ws.Cell(row, 4).Value = f.Path;
        ws.Cell(row, 5).Value = fp?.Config ?? "";
        ws.Cell(row, 6).Value = f.Sha;
        ws.Cell(row, 7).Value = fp?.Material ?? "";
        ws.Cell(row, 8).Value = bb.Length > 0 ? Math.Round(bb[0] * 1000, 2) : 0;
        ws.Cell(row, 9).Value = bb.Length > 1 ? Math.Round(bb[1] * 1000, 2) : 0;
        ws.Cell(row, 10).Value = bb.Length > 2 ? Math.Round(bb[2] * 1000, 2) : 0;
        ws.Cell(row, 11).Value = fp != null ? Math.Round(fp.Volume * 1e6, 4) : 0;
        ws.Cell(row, 12).Value = fp != null ? Math.Round(fp.SurfaceArea * 1e4, 4) : 0;
        ws.Cell(row, 13).Value = fp?.Mass.HasValue == true ? Math.Round(fp.Mass.Value * 1000, 4) : "";
        ws.Cell(row, 14).Value = fp?.SolidBodies ?? 0;
        ws.Cell(row, 15).Value = fp?.Faces ?? 0;
        ws.Cell(row, 16).Value = fp?.Edges ?? 0;
        ws.Cell(row, 17).Value = fp?.Verts ?? 0;
        ws.Cell(row, 18).Value = fp?.Features ?? 0;
        ws.Cell(row, 19).Value = fp?.ExtractorLabel ?? "";
        ws.Cell(row, 20).Value = f.Status;
        row++;
    }
    FormatTable(ws, "SourceFiles", 1, row - 1, h.Length);
}

// PairComparisons
{
    var ws = wb.Worksheets.Add("PairComparisons");
    string[] h = { "File A", "Config A", "File B", "Config B",
                   "Coarse Score", "Classification", "Confidence", "Reason",
                   "Comparator", "Tolerance Profile" };
    WriteHeaders(ws, h);
    var fpById = fps.ToDictionary(f => f.Id);
    var fileById2 = files.ToDictionary(f => f.Id);
    int row = 2;
    foreach (var p in pairs.OrderByDescending(p => p.Score))
    {
        fpById.TryGetValue(p.FpAId, out var fpA);
        fpById.TryGetValue(p.FpBId, out var fpB);
        fileById2.TryGetValue(fpA?.FileId ?? "", out var fa);
        fileById2.TryGetValue(fpB?.FileId ?? "", out var fb);
        ws.Cell(row, 1).Value = fa?.Name ?? "";
        ws.Cell(row, 2).Value = fpA?.Config ?? "";
        ws.Cell(row, 3).Value = fb?.Name ?? "";
        ws.Cell(row, 4).Value = fpB?.Config ?? "";
        ws.Cell(row, 5).Value = Math.Round(p.Score, 4);
        ws.Cell(row, 6).Value = p.Classification;
        ws.Cell(row, 7).Value = p.Confidence.HasValue ? Math.Round(p.Confidence.Value, 4) : "";
        ws.Cell(row, 8).Value = p.Reason;
        ws.Cell(row, 9).Value = p.CompVer;
        ws.Cell(row, 10).Value = p.TolProfile;
        if (p.Classification == "BinaryDuplicate")
            ws.Row(row).Style.Fill.BackgroundColor = XLColor.LightGreen;
        row++;
    }
    FormatTable(ws, "PairComparisons", 1, row - 1, h.Length);
}

// NeedsReview
{
    var ws = wb.Worksheets.Add("NeedsReview");
    string[] h = { "Cluster ID", "Canonical Name", "Classification", "Review Status", "Notes" };
    WriteHeaders(ws, h);
    int row = 2;
    foreach (var c in clusters.Where(c => c.ReviewStatus == "NeedsReview"))
    {
        ws.Cell(row, 1).Value = c.Id;
        ws.Cell(row, 2).Value = c.Name;
        ws.Cell(row, 3).Value = c.Classification;
        ws.Cell(row, 4).Value = c.ReviewStatus;
        ws.Cell(row, 5).Value = c.Note;
        ws.Row(row).Style.Fill.BackgroundColor = XLColor.LightYellow;
        row++;
    }
    FormatTable(ws, "NeedsReview", 1, row - 1, h.Length);
}

// RunMetadata
{
    var ws = wb.Worksheets.Add("RunMetadata");
    ws.Column(1).Width = 28;
    ws.Column(2).Width = 70;
    void KV(int r, string k, string v)
    {
        ws.Cell(r, 1).Value = k;
        ws.Cell(r, 2).Value = v;
        ws.Cell(r, 1).Style.Font.Bold = true;
    }
    KV(1, "Run ID", runMeta.Id);
    KV(2, "Status", runMeta.Status);
    KV(3, "Started UTC", runMeta.Started);
    KV(4, "Ended UTC", runMeta.Ended);
    KV(5, "Source Roots", runMeta.Roots);
    KV(6, "App Version", runMeta.AppVersion);
    KV(7, "Files Scanned", files.Count.ToString());
    KV(8, "Clusters Found", clusters.Count.ToString());
    KV(9, "Candidate Pairs", pairs.Count.ToString());
    KV(10, "Export Generated", DateTime.Now.ToString("O"));
}

var outPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
    $"PartMatcher_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");

wb.SaveAs(outPath);
Console.WriteLine($"Saved to Desktop: {Path.GetFileName(outPath)}");
System.Diagnostics.Process.Start(
    new System.Diagnostics.ProcessStartInfo(outPath) { UseShellExecute = true });
return 0;

// â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

static List<T> Query<T>(SqliteConnection conn, string sql,
    Func<SqliteDataReader, T> map, params (string name, object val)[] parms)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, val) in parms) cmd.Parameters.AddWithValue(name, val);
    using var r = cmd.ExecuteReader();
    var list = new List<T>();
    while (r.Read()) list.Add(map(r));
    return list;
}

static void WriteHeaders(IXLWorksheet ws, string[] headers)
{
    for (int i = 0; i < headers.Length; i++)
    {
        var cell = ws.Cell(1, i + 1);
        cell.Value = headers[i];
        cell.Style.Font.Bold = true;
        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E79");
        cell.Style.Font.FontColor = XLColor.White;
    }
    ws.Row(1).Height = 18;
    ws.SheetView.FreezeRows(1);
}

static void FormatTable(IXLWorksheet ws, string name, int fr, int lr, int cols)
{
    if (lr < fr) return;
    var tbl = ws.Range(fr, 1, lr, cols).CreateTable(name);
    tbl.Theme = XLTableTheme.TableStyleMedium2;
    ws.Columns(1, cols).AdjustToContents();
    for (int c = 1; c <= cols; c++)
        if (ws.Column(c).Width > 55) ws.Column(c).Width = 55;
}

static double[] ParseBB(string? json)
{
    if (string.IsNullOrEmpty(json)) return Array.Empty<double>();
    try { return System.Text.Json.JsonSerializer.Deserialize<double[]>(json) ?? Array.Empty<double>(); }
    catch { return Array.Empty<double>(); }
}

