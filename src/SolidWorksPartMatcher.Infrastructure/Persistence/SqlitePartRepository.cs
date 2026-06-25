using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Infrastructure.Persistence;

public sealed class SqlitePartRepository : IPartRepository, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ILogger<SqlitePartRepository> _logger;

    public SqlitePartRepository(string databasePath, ILogger<SqlitePartRepository> logger)
    {
        _logger = logger;
        _conn = new SqliteConnection($"Data Source={databasePath}");
        _conn.Open();
        ApplyMigrations();
    }

    private void ApplyMigrations()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS scan_runs (
                id TEXT PRIMARY KEY,
                started_utc TEXT NOT NULL,
                ended_utc TEXT,
                source_roots TEXT NOT NULL,
                app_version TEXT NOT NULL,
                status TEXT NOT NULL,
                scan_settings_json TEXT
            );

            CREATE TABLE IF NOT EXISTS scanned_files (
                id TEXT PRIMARY KEY,
                scan_run_id TEXT NOT NULL,
                normalized_path TEXT NOT NULL,
                file_name TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                last_modified_utc TEXT NOT NULL,
                sha256 TEXT,
                discovery_root TEXT NOT NULL,
                status TEXT NOT NULL,
                error TEXT,
                FOREIGN KEY(scan_run_id) REFERENCES scan_runs(id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_scanned_files_path ON scanned_files(scan_run_id, normalized_path);

            CREATE TABLE IF NOT EXISTS fingerprints (
                id TEXT PRIMARY KEY,
                scanned_file_id TEXT NOT NULL,
                file_sha256 TEXT NOT NULL,
                config_name TEXT NOT NULL,
                extractor_version INTEGER NOT NULL,
                solid_body_count INTEGER NOT NULL,
                surface_body_count INTEGER NOT NULL,
                sorted_bounding_box_m TEXT NOT NULL,
                volume_m3 REAL NOT NULL,
                surface_area_m2 REAL NOT NULL,
                mass_kg REAL,
                center_of_mass_m TEXT,
                face_count INTEGER NOT NULL,
                edge_count INTEGER NOT NULL,
                vertex_count INTEGER NOT NULL,
                feature_count INTEGER NOT NULL,
                feature_type_histogram TEXT NOT NULL,
                material TEXT,
                custom_properties TEXT NOT NULL,
                solidworks_version TEXT NOT NULL,
                extractor_version_label TEXT NOT NULL,
                extracted_utc TEXT NOT NULL,
                chirality_sign REAL,
                com_offset_in_bb TEXT,
                sketch_text_cut_count INTEGER NOT NULL DEFAULT 0,
                suppressed_geometry_json TEXT,
                FOREIGN KEY(scanned_file_id) REFERENCES scanned_files(id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_fingerprints_per_file
                ON fingerprints(scanned_file_id, config_name, extractor_version);

            CREATE TABLE IF NOT EXISTS candidate_pairs (
                id TEXT PRIMARY KEY,
                scan_run_id TEXT NOT NULL,
                fingerprint_a_id TEXT NOT NULL,
                fingerprint_b_id TEXT NOT NULL,
                coarse_score REAL NOT NULL,
                matched_buckets TEXT NOT NULL,
                classification TEXT NOT NULL,
                confidence REAL,
                classification_reason TEXT,
                comparator_version TEXT,
                tolerance_profile TEXT,
                FOREIGN KEY(scan_run_id) REFERENCES scan_runs(id)
            );

            CREATE TABLE IF NOT EXISTS clusters (
                id TEXT PRIMARY KEY,
                scan_run_id TEXT NOT NULL,
                canonical_name TEXT NOT NULL,
                classification TEXT NOT NULL,
                representative_fingerprint_id TEXT NOT NULL,
                review_status TEXT NOT NULL,
                reviewer_note TEXT,
                reviewed_utc TEXT,
                reviewer_name TEXT,
                FOREIGN KEY(scan_run_id) REFERENCES scan_runs(id)
            );

            CREATE TABLE IF NOT EXISTS cluster_members (
                cluster_id TEXT NOT NULL,
                fingerprint_id TEXT NOT NULL,
                is_representative INTEGER NOT NULL,
                PRIMARY KEY(cluster_id, fingerprint_id),
                FOREIGN KEY(cluster_id) REFERENCES clusters(id)
            );

            -- On a fresh database the CREATE TABLE already includes all columns
            -- added by migrations v2-v6, so pre-mark them as applied.
            -- OR IGNORE is a no-op when the record already exists (existing databases).
            INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES
                (2, '2025-01-01T00:00:00Z'),
                (3, '2025-01-01T00:00:00Z'),
                (4, '2025-01-01T00:00:00Z'),
                (5, '2025-01-01T00:00:00Z'),
                (6, '2025-01-01T00:00:00Z');
            """;
        cmd.ExecuteNonQuery();

        // Migration v2: replace SHA-based fingerprint index with per-file index
        // so two files with the same SHA each get their own fingerprint record.
        MigrateIfNeeded(2, """
            DROP INDEX IF EXISTS ix_fingerprints_cache_key;
            CREATE UNIQUE INDEX IF NOT EXISTS ix_fingerprints_per_file
                ON fingerprints(scanned_file_id, config_name, extractor_version);
            """);

        // Migration v3: add chirality_sign column for mirror-pair detection.
        MigrateIfNeeded(3, "ALTER TABLE fingerprints ADD COLUMN chirality_sign REAL;");

        // Migration v4: add com_offset_in_bb column (JSON array, sorted by BB dim size).
        MigrateIfNeeded(4, "ALTER TABLE fingerprints ADD COLUMN com_offset_in_bb TEXT;");

        // Migration v5: add sketch_text_cut_count for engraving detection.
        MigrateIfNeeded(5, "ALTER TABLE fingerprints ADD COLUMN sketch_text_cut_count INTEGER NOT NULL DEFAULT 0;");

        // Migration v6: suppressed_geometry_json stores body geometry after temporarily
        // suppressing text-engraving features, for engraving-aware comparison.
        MigrateIfNeeded(6, "ALTER TABLE fingerprints ADD COLUMN suppressed_geometry_json TEXT;");
    }

    private void MigrateIfNeeded(int version, string sql)
    {
        using var check = _conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=@v";
        check.Parameters.AddWithValue("@v", version);
        if ((long)(check.ExecuteScalar() ?? 0L) > 0) return;

        using var apply = _conn.CreateCommand();
        apply.CommandText = sql;
        apply.ExecuteNonQuery();

        using var record = _conn.CreateCommand();
        record.CommandText = "INSERT INTO schema_migrations(version, applied_utc) VALUES(@v, @t)";
        record.Parameters.AddWithValue("@v", version);
        record.Parameters.AddWithValue("@t", DateTime.UtcNow.ToString("O"));
        record.ExecuteNonQuery();
    }

    // ── Scan runs ──────────────────────────────────────────────────────────

    public async Task<ScanRun> CreateScanRunAsync(ScanRun run, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO scan_runs (id, started_utc, ended_utc, source_roots, app_version, status, scan_settings_json)
            VALUES (@id, @started, @ended, @roots, @ver, @status, @settings)
            """;
        cmd.Parameters.AddWithValue("@id", run.Id.ToString());
        cmd.Parameters.AddWithValue("@started", run.StartedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@ended", run.EndedUtc.HasValue ? run.EndedUtc.Value.ToString("O") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@roots", JsonSerializer.Serialize(run.SourceRoots));
        cmd.Parameters.AddWithValue("@ver", run.AppVersion);
        cmd.Parameters.AddWithValue("@status", run.Status.ToString());
        cmd.Parameters.AddWithValue("@settings", run.ScanSettingsJson ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        return run;
    }

    public async Task UpdateScanRunStatusAsync(Guid runId, ScanStatus status, DateTime? endedUtc, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE scan_runs SET status=@status, ended_utc=@ended WHERE id=@id";
        cmd.Parameters.AddWithValue("@status", status.ToString());
        cmd.Parameters.AddWithValue("@ended", endedUtc.HasValue ? endedUtc.Value.ToString("O") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@id", runId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Scanned files ──────────────────────────────────────────────────────

    public async Task UpsertScannedFileAsync(ScannedFile file, Guid scanRunId, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO scanned_files (id, scan_run_id, normalized_path, file_name, size_bytes,
                last_modified_utc, sha256, discovery_root, status, error)
            VALUES (@id, @runId, @path, @name, @size, @modified, @sha, @root, @status, @err)
            ON CONFLICT(scan_run_id, normalized_path) DO UPDATE SET
                sha256=excluded.sha256, status=excluded.status, error=excluded.error,
                size_bytes=excluded.size_bytes, last_modified_utc=excluded.last_modified_utc
            """;
        cmd.Parameters.AddWithValue("@id", file.Id.ToString());
        cmd.Parameters.AddWithValue("@runId", scanRunId.ToString());
        cmd.Parameters.AddWithValue("@path", file.NormalizedPath);
        cmd.Parameters.AddWithValue("@name", file.FileName);
        cmd.Parameters.AddWithValue("@size", file.SizeBytes);
        cmd.Parameters.AddWithValue("@modified", file.LastModifiedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@sha", file.Sha256 ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@root", file.DiscoveryRoot);
        cmd.Parameters.AddWithValue("@status", file.Status.ToString());
        cmd.Parameters.AddWithValue("@err", file.Error ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<ScannedFile?> GetScannedFileByPathAsync(string normalizedPath, CancellationToken ct)
        => Task.FromResult<ScannedFile?>(null);

    public async Task<IReadOnlyList<ScannedFile>> GetAllScannedFilesAsync(Guid scanRunId, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM scanned_files WHERE scan_run_id=@runId ORDER BY file_name";
        cmd.Parameters.AddWithValue("@runId", scanRunId.ToString());
        var results = new List<ScannedFile>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            results.Add(new ScannedFile(
                Id: Guid.Parse(r.GetString(0)),
                NormalizedPath: r.GetString(2),
                FileName: r.GetString(3),
                SizeBytes: r.GetInt64(4),
                LastModifiedUtc: DateTime.Parse(r.GetString(5)),
                Sha256: r.IsDBNull(6) ? null : r.GetString(6),
                DiscoveryRoot: r.GetString(7),
                Status: Enum.Parse<FileStatus>(r.GetString(8)),
                Error: r.IsDBNull(9) ? null : r.GetString(9)));
        return results;
    }

    // ── Fingerprints ───────────────────────────────────────────────────────

    public async Task UpsertFingerprintAsync(PartFingerprint fp, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO fingerprints (id, scanned_file_id, file_sha256, config_name, extractor_version,
                solid_body_count, surface_body_count, sorted_bounding_box_m, volume_m3, surface_area_m2,
                mass_kg, center_of_mass_m, face_count, edge_count, vertex_count, feature_count,
                feature_type_histogram, material, custom_properties, solidworks_version,
                extractor_version_label, extracted_utc, chirality_sign, com_offset_in_bb,
                sketch_text_cut_count, suppressed_geometry_json)
            VALUES (@id,@fileId,@sha,@cfg,@extVer,@solid,@surface,@bb,@vol,@sa,@mass,@com,
                    @face,@edge,@vertex,@feat,@featHist,@mat,@props,@swVer,@extLabel,@extracted,@chirality,@comOff,
                    @sketchTextCuts,@suppGeom)
            ON CONFLICT(scanned_file_id, config_name, extractor_version) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("@id", fp.Id.ToString());
        cmd.Parameters.AddWithValue("@fileId", fp.ScannedFileId.ToString());
        cmd.Parameters.AddWithValue("@sha", fp.FileSha256);
        cmd.Parameters.AddWithValue("@cfg", fp.ConfigName);
        cmd.Parameters.AddWithValue("@extVer", fp.ExtractorVersion);
        cmd.Parameters.AddWithValue("@solid", fp.SolidBodyCount);
        cmd.Parameters.AddWithValue("@surface", fp.SurfaceBodyCount);
        cmd.Parameters.AddWithValue("@bb", JsonSerializer.Serialize(fp.SortedBoundingBoxM));
        cmd.Parameters.AddWithValue("@vol", fp.VolumeM3);
        cmd.Parameters.AddWithValue("@sa", fp.SurfaceAreaM2);
        cmd.Parameters.AddWithValue("@mass", fp.MassKg.HasValue ? fp.MassKg.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@com", fp.CenterOfMassM != null ? JsonSerializer.Serialize(fp.CenterOfMassM) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@face", fp.FaceCount);
        cmd.Parameters.AddWithValue("@edge", fp.EdgeCount);
        cmd.Parameters.AddWithValue("@vertex", fp.VertexCount);
        cmd.Parameters.AddWithValue("@feat", fp.FeatureCount);
        cmd.Parameters.AddWithValue("@featHist", JsonSerializer.Serialize(fp.FeatureTypeHistogram));
        cmd.Parameters.AddWithValue("@mat", fp.Material ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@props", JsonSerializer.Serialize(fp.CustomProperties));
        cmd.Parameters.AddWithValue("@swVer", fp.SolidWorksVersion);
        cmd.Parameters.AddWithValue("@extLabel", fp.ExtractorVersionLabel);
        cmd.Parameters.AddWithValue("@extracted", fp.ExtractedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@chirality", fp.ChiralitySign.HasValue ? fp.ChiralitySign.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@comOff", fp.CoMOffsetInBB != null ? JsonSerializer.Serialize(fp.CoMOffsetInBB) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sketchTextCuts", fp.SketchTextCutCount);
        string? suppGeomJson = null;
        if (fp.SuppressedVolumeM3.HasValue)
        {
            suppGeomJson = JsonSerializer.Serialize(new SuppressedGeometryData(
                fp.SuppressedSolidBodyCount ?? 0,
                fp.SuppressedBoundingBoxM ?? [],
                fp.SuppressedVolumeM3.Value,
                fp.SuppressedSurfaceAreaM2 ?? 0,
                fp.SuppressedFaceCount ?? 0,
                fp.SuppressedEdgeCount ?? 0,
                fp.SuppressedVertexCount ?? 0));
        }
        cmd.Parameters.AddWithValue("@suppGeom", suppGeomJson ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<PartFingerprint?> GetFingerprintAsync(string sha256, string configName, int extractorVersion, CancellationToken ct)
        => Task.FromResult<PartFingerprint?>(null);

    public async Task<IReadOnlyList<PartFingerprint>> GetAllFingerprintsAsync(Guid scanRunId, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT f.* FROM fingerprints f
            INNER JOIN scanned_files sf ON sf.id = f.scanned_file_id
            WHERE sf.scan_run_id = @runId
            """;
        cmd.Parameters.AddWithValue("@runId", scanRunId.ToString());
        var results = new List<PartFingerprint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadFingerprint(reader));
        return results;
    }

    private static PartFingerprint ReadFingerprint(SqliteDataReader r)
    {
        SuppressedGeometryData? suppGeom = null;
        if (r.FieldCount > 25 && !r.IsDBNull(25))
            suppGeom = JsonSerializer.Deserialize<SuppressedGeometryData>(r.GetString(25));

        return new PartFingerprint(
            Id: Guid.Parse(r.GetString(0)),
            ScannedFileId: Guid.Parse(r.GetString(1)),
            FileSha256: r.GetString(2),
            ConfigName: r.GetString(3),
            ExtractorVersion: r.GetInt32(4),
            SolidBodyCount: r.GetInt32(5),
            SurfaceBodyCount: r.GetInt32(6),
            SortedBoundingBoxM: JsonSerializer.Deserialize<double[]>(r.GetString(7))!,
            VolumeM3: r.GetDouble(8),
            SurfaceAreaM2: r.GetDouble(9),
            MassKg: r.IsDBNull(10) ? null : r.GetDouble(10),
            CenterOfMassM: r.IsDBNull(11) ? null : JsonSerializer.Deserialize<double[]>(r.GetString(11)),
            FaceCount: r.GetInt32(12),
            EdgeCount: r.GetInt32(13),
            VertexCount: r.GetInt32(14),
            FeatureCount: r.GetInt32(15),
            FeatureTypeHistogram: JsonSerializer.Deserialize<Dictionary<string, int>>(r.GetString(16))!,
            Material: r.IsDBNull(17) ? null : r.GetString(17),
            CustomProperties: JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(18))!,
            SolidWorksVersion: r.GetString(19),
            ExtractorVersionLabel: r.GetString(20),
            ExtractedUtc: DateTime.Parse(r.GetString(21)),
            ChiralitySign: r.FieldCount > 22 && !r.IsDBNull(22) ? r.GetDouble(22) : null,
            CoMOffsetInBB: r.FieldCount > 23 && !r.IsDBNull(23)
                ? JsonSerializer.Deserialize<double[]>(r.GetString(23)) : null,
            SketchTextCutCount: r.FieldCount > 24 ? r.GetInt32(24) : 0,
            SuppressedSolidBodyCount: suppGeom?.SolidBodyCount,
            SuppressedBoundingBoxM: suppGeom?.BoundingBoxM,
            SuppressedVolumeM3: suppGeom?.VolumeM3,
            SuppressedSurfaceAreaM2: suppGeom?.SurfaceAreaM2,
            SuppressedFaceCount: suppGeom?.FaceCount,
            SuppressedEdgeCount: suppGeom?.EdgeCount,
            SuppressedVertexCount: suppGeom?.VertexCount);
    }

    // ── Candidate pairs ────────────────────────────────────────────────────

    public async Task UpsertCandidatePairAsync(CandidatePair pair, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO candidate_pairs
                (id, scan_run_id, fingerprint_a_id, fingerprint_b_id, coarse_score,
                 matched_buckets, classification, confidence, classification_reason,
                 comparator_version, tolerance_profile)
            VALUES (@id,@runId,@a,@b,@score,@buckets,@cls,@conf,@reason,@compVer,@tol)
            """;
        cmd.Parameters.AddWithValue("@id", pair.Id.ToString());
        cmd.Parameters.AddWithValue("@runId", pair.ScanRunId.ToString());
        cmd.Parameters.AddWithValue("@a", pair.FingerprintAId.ToString());
        cmd.Parameters.AddWithValue("@b", pair.FingerprintBId.ToString());
        cmd.Parameters.AddWithValue("@score", pair.CoarseScore);
        cmd.Parameters.AddWithValue("@buckets", JsonSerializer.Serialize(pair.MatchedBuckets));
        cmd.Parameters.AddWithValue("@cls", pair.Classification.ToString());
        cmd.Parameters.AddWithValue("@conf", pair.Confidence.HasValue ? pair.Confidence.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@reason", pair.ClassificationReason ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@compVer", pair.ComparatorVersion ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@tol", pair.ToleranceProfile ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<CandidatePair>> GetCandidatePairsAsync(Guid scanRunId, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM candidate_pairs WHERE scan_run_id=@runId";
        cmd.Parameters.AddWithValue("@runId", scanRunId.ToString());
        var results = new List<CandidatePair>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new CandidatePair(
                Id: Guid.Parse(reader.GetString(0)),
                ScanRunId: Guid.Parse(reader.GetString(1)),
                FingerprintAId: Guid.Parse(reader.GetString(2)),
                FingerprintBId: Guid.Parse(reader.GetString(3)),
                CoarseScore: reader.GetDouble(4),
                MatchedBuckets: JsonSerializer.Deserialize<string[]>(reader.GetString(5))!,
                Classification: Enum.Parse<PartClassification>(reader.GetString(6)),
                Confidence: reader.IsDBNull(7) ? null : reader.GetDouble(7),
                ClassificationReason: reader.IsDBNull(8) ? null : reader.GetString(8),
                ComparatorVersion: reader.IsDBNull(9) ? null : reader.GetString(9),
                ToleranceProfile: reader.IsDBNull(10) ? null : reader.GetString(10)));
        return results;
    }

    // ── Clusters ───────────────────────────────────────────────────────────

    public async Task UpsertClusterAsync(PartCluster cluster, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO clusters
                (id, scan_run_id, canonical_name, classification, representative_fingerprint_id,
                 review_status, reviewer_note, reviewed_utc, reviewer_name)
            VALUES (@id,@runId,@name,@cls,@rep,@revStatus,@note,@revUtc,@reviewer)
            """;
        cmd.Parameters.AddWithValue("@id", cluster.Id.ToString());
        cmd.Parameters.AddWithValue("@runId", cluster.ScanRunId.ToString());
        cmd.Parameters.AddWithValue("@name", cluster.CanonicalName);
        cmd.Parameters.AddWithValue("@cls", cluster.Classification.ToString());
        cmd.Parameters.AddWithValue("@rep", cluster.RepresentativeFingerprintId.ToString());
        cmd.Parameters.AddWithValue("@revStatus", cluster.ReviewStatus.ToString());
        cmd.Parameters.AddWithValue("@note", cluster.ReviewerNote ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@revUtc", cluster.ReviewedUtc.HasValue ? cluster.ReviewedUtc.Value.ToString("O") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@reviewer", cluster.ReviewerName ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertClusterMemberAsync(ClusterMember member, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO cluster_members (cluster_id, fingerprint_id, is_representative)
            VALUES (@cid, @fid, @rep)
            """;
        cmd.Parameters.AddWithValue("@cid", member.ClusterId.ToString());
        cmd.Parameters.AddWithValue("@fid", member.FingerprintId.ToString());
        cmd.Parameters.AddWithValue("@rep", member.IsRepresentative ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertClusterWithMembersAsync(
        PartCluster cluster, IReadOnlyList<ClusterMember> members, CancellationToken ct)
    {
        await using var tx = await _conn.BeginTransactionAsync(ct);
        try
        {
            await UpsertClusterAsync(cluster, ct);
            foreach (var m in members)
                await UpsertClusterMemberAsync(m, ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<PartCluster>> GetClustersAsync(Guid scanRunId, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM clusters WHERE scan_run_id=@runId ORDER BY canonical_name";
        cmd.Parameters.AddWithValue("@runId", scanRunId.ToString());
        var results = new List<PartCluster>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new PartCluster(
                Id: Guid.Parse(reader.GetString(0)),
                ScanRunId: Guid.Parse(reader.GetString(1)),
                CanonicalName: reader.GetString(2),
                Classification: Enum.Parse<PartClassification>(reader.GetString(3)),
                RepresentativeFingerprintId: Guid.Parse(reader.GetString(4)),
                ReviewStatus: Enum.Parse<ReviewStatus>(reader.GetString(5)),
                ReviewerNote: reader.IsDBNull(6) ? null : reader.GetString(6),
                ReviewedUtc: reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7)),
                ReviewerName: reader.IsDBNull(8) ? null : reader.GetString(8)));
        return results;
    }

    public async Task<IReadOnlyList<ClusterMember>> GetClusterMembersAsync(Guid clusterId, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM cluster_members WHERE cluster_id=@cid";
        cmd.Parameters.AddWithValue("@cid", clusterId.ToString());
        var results = new List<ClusterMember>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new ClusterMember(
                ClusterId: Guid.Parse(reader.GetString(0)),
                FingerprintId: Guid.Parse(reader.GetString(1)),
                IsRepresentative: reader.GetInt32(2) != 0));
        return results;
    }

    public async Task UpdateClusterReviewAsync(Guid clusterId, ReviewStatus status, string? note, string? reviewer, DateTime reviewedUtc, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE clusters SET review_status=@status, reviewer_note=@note,
                reviewer_name=@reviewer, reviewed_utc=@utc WHERE id=@id
            """;
        cmd.Parameters.AddWithValue("@status", status.ToString());
        cmd.Parameters.AddWithValue("@note", note ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@reviewer", reviewer ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@utc", reviewedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@id", clusterId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateClusterCanonicalNameAsync(Guid clusterId, string canonicalName, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE clusters SET canonical_name=@name WHERE id=@id";
        cmd.Parameters.AddWithValue("@name", canonicalName);
        cmd.Parameters.AddWithValue("@id", clusterId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public void Dispose() => _conn.Dispose();

    private sealed record SuppressedGeometryData(
        int SolidBodyCount,
        double[] BoundingBoxM,
        double VolumeM3,
        double SurfaceAreaM2,
        int FaceCount,
        int EdgeCount,
        int VertexCount);
}
