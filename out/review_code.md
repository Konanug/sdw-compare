## Summary

The codebase is structurally sound and follows the design spec closely, but contains several correctness bugs — two of which will silently produce wrong results in production (false ExactGeometryMatch for mirrored parts; incomplete cluster membership writes) — plus an untransacted multi-step DB write that can leave the database in a torn state.

---

## Issues

### ScanOrchestrationService.cs

- **[severity: high] Correctness — Mirror detection is unreliable and produces false ExactGeometryMatch.**
  `HasMirrorFeature` checks whether the SolidWorks feature tree contains a "MirrorSolid", "Mirror", or "BodyMirror" feature. A mirrored part (left-hand bracket modelled by mirroring a right-hand bracket) will have that feature and `mirrorA != mirrorB` will be true. But the part created without the mirror feature — the original right-hand bracket — will score ≥0.90 against another copy of itself and be correctly classified. The failure case is: two files that are both mirrored (both have the mirror feature, so `mirrorA == mirrorB == true`), or two independently modelled handed parts where neither file uses a mirror feature at all. In either case `likelyMirrorPair` is `false` and a score ≥0.90 produces `ExactGeometryMatch`. The only robust fingerprint-level discriminator is the sign of the determinant of the principal inertia axes (chirality). Until that field is captured and compared, mirror detection cannot be trusted and the `MirrorOrHandedVariant` path will have false negatives. The existing feature-histogram check should be documented as a partial heuristic only, not a gating condition before `ExactGeometryMatch`.

- **[severity: high] Correctness — `IsInCluster` does not follow transitive union-find edges.**
  `IsInCluster(fpId, clusterId, pairs, fingerprints)` only looks for a direct pair edge between `fpId` and `clusterId`. For a three-member cluster A→B→C (where A is the root, and C was joined via B, never directly compared to A), C returns `false` here. As a result, `UpsertClusterMemberAsync` is never called for C, so C is silently dropped from cluster membership in the database even though `UnionFindClusterBuilder` already computed the correct union correctly. The fix is to derive cluster membership directly from `UnionFindClusterBuilder`'s output instead of re-deriving it with a separate graph walk. `BuildClusters` already returns fully resolved clusters — the orchestrator should pass member lists out from that call rather than recomputing them independently.

- **[severity: medium] Correctness — `PerFileGuid` produces colliding IDs when multiple files share the same `fileId`.**
  MD5 of a GUID is deterministic: if two `ScannedFile` records somehow share the same `Id` (which should not happen, but the orchestrator creates `file.Id` at discovery time with `Guid.NewGuid()` and then later reuses it), their fingerprints would be identical GUIDs. This is low probability, but the real concern is semantic: deriving a child GUID from a parent GUID via MD5 is not a standard pattern and gives no guarantee of uniqueness across different parent IDs that happen to produce the same 128-bit hash. Prefer `Guid.NewGuid()` for each fingerprint record.

- **[severity: medium] Correctness — Fingerprinting always uses configuration "Default".**
  `ExtractAsync(group[0], "Default", cancellationToken)` hard-codes "Default" regardless of the actual configurations present in the file. Parts with multiple configurations will never have their non-default configurations fingerprinted in Stage 1. This is noted implicitly in the spec (indexed unit is `(file, config)`), but no warning is logged and the limitation is invisible to the operator.

- **[severity: low] Correctness — `CanonicalNameService.Suggest` calls `Path.GetFileNameWithoutExtension` on `ConfigName`.**
  `m.ConfigName` is a SolidWorks configuration name like "Default" or "Long Bolt", not a file path. `Path.GetFileNameWithoutExtension("Default")` returns "Default" on Windows (no extension to strip), so the result is correct today, but it will silently mangle a config name that contains a dot, e.g., "Rev.2" becomes "Rev". Replace with a direct use of `m.ConfigName`.

### BucketCandidateBlocker.cs

- **[severity: medium] Correctness — Only the first matched bucket key is recorded per pair; additional matched buckets are discarded.**
  When deduplication (`seen.Add(key)`) skips a pair on the second (or later) bucket iteration, the pair is already in `results` with `[bucket]` from the first match only. Later code stores `MatchedBuckets` in the database and exports it to Excel for auditability. An auditor looking at that field will see only one bucket when two or three actually matched, making it harder to understand why the pair was promoted. Fix: collect pairs into a `Dictionary<(Guid,Guid), List<string>>` during the loop and convert to results after, accumulating all matched bucket keys.

- **[severity: low] Performance — Neighbor bucket generation produces 26 neighbors per fingerprint (3³-1), causing O(27n²) worst-case pair enumeration in the deduplication loop.**
  For large scans this compounds: with 1 000 parts all landing in the same volume bucket, the outer loop over buckets visits each pair up to 27 times (once per bucket) before deduplication discards 26 of those visits. This is bounded by `O(n² × 27)` comparisons of the `seen` set, which is acceptable for typical part library sizes (hundreds to low thousands of files). No change needed unless scans exceed ~5 000 files.

### WeightedCandidateScorer.cs

- **[severity: medium] Correctness — `ScalarSimilarity` returns 1.0 when both inputs are zero or both non-positive, even when they are different negative values.**
  The early return `if (a <= 0 && b <= 0) return 1.0` is intended to handle "no data" cases (volume/area are not physically negative), but it conflates "both zero" with "both negative". If a fingerprint has a negative volume due to a COM API error, two parts with different negative volumes would score 1.0 on that dimension. The guard should be `if (a == 0 && b == 0) return 1.0` or rely solely on the `max == 0` check below it.

- **[severity: low] Correctness — `FilenameSimilarity` operates on `ConfigName`, not the source filename.**
  The scorer receives `PartFingerprint` objects, and `a.ConfigName` / `b.ConfigName` are SolidWorks configuration names, not the original filenames. The scoring weight comment in the spec reads "filename tokens (ranking only)". Comparing configuration names (which are usually "Default" for most parts) means this sub-score is nearly always 1.0 and contributes noise rather than signal. The `files` side-channel needed to resolve the actual filename is not passed to `ICandidateScorer`. This is a design issue: either thread the filename through `PartFingerprint`, or accept that this dimension is currently non-functional and zero out its weight.

### UnionFindClusterBuilder.cs

- **[severity: medium] Correctness — `pairsByIds` dictionary can silently lose pairs when two pairs with the same canonical key exist.**
  `pairs.ToDictionary(p => ...)` will throw `ArgumentException` if two `CandidatePair` records have the same ordered `(FingerprintAId, FingerprintBId)` key (e.g., if `UpsertCandidatePairAsync` was called twice for the same logical pair). The `candidate_pairs` table uses `INSERT OR REPLACE` keyed on `id` (a new GUID each time), not on `(fingerprint_a_id, fingerprint_b_id)`, so duplicates can accumulate across re-runs in the same DB. Use `GroupBy` + `First` or add a DB-level unique constraint on `(fingerprint_a_id, fingerprint_b_id, scan_run_id)`.

- **[severity: low] Correctness — `DetermineClassification` checks only adjacent member pairs from the pairs dictionary, but a 3+ member cluster joined via intermediate nodes may have no direct pair record between some members.**
  If A was merged with B (ExactGeometryMatch) and B was merged with C (ExactGeometryMatch), but A and C were never directly compared, `DetermineClassification` will not find a pair for `(A, C)` and may fall through to `PossibleMatch` for the cluster, even though it was legitimately formed by two ExactGeometryMatch edges. The returned cluster classification would be wrong. Fix: return `ExactGeometryMatch` as soon as any pair in the cluster is an `ExactGeometryMatch`, which the current code already does when it can find a pair — the gap is only when the pair is missing because the members were connected transitively. The most correct fix is to find any pair for any two members that is `ExactGeometryMatch`, and the current double-loop already does that for members that were directly compared; the implicit assumption is that the union-find only merges via direct pairs, so any two members in the same cluster have at least one witnessed pair. This is actually correct given that union-find merges only happen when a pair exists — document the invariant.

### SqlitePartRepository.cs

- **[severity: high] Correctness — Cluster and its members are written without a transaction.**
  `UpsertClusterAsync` and each subsequent `UpsertClusterMemberAsync` call are issued as separate, auto-committed statements on the shared connection. If the process is killed, crashes, or the cancellation token fires between writing the cluster row and writing its members, the database will contain a cluster with no members. On a re-run, the orphaned cluster row will be replaced (INSERT OR REPLACE) but previous member rows will remain for the old cluster ID (since cluster_id is a foreign key and no cascade delete is set). Wrap the entire cluster + members write in a `BeginTransaction` / `Commit` block, with rollback on failure.

- **[severity: high] Correctness — `GetAllScannedFilesAsync` reads columns by ordinal position but the SELECT is `SELECT *`.**
  Column ordinals 0–9 are assumed to correspond to `id, scan_run_id, normalized_path, file_name, size_bytes, last_modified_utc, sha256, discovery_root, status, error` in that exact order. If a future migration adds a column (even with ALTER TABLE ADD COLUMN), SQLite appends it at the end, so `SELECT *` ordinal mapping will remain correct only as long as no column is inserted in the middle. The same pattern is used in `GetCandidatePairsAsync`, `GetClustersAsync`, `GetClusterMembersAsync`, and `ReadFingerprint`. This is fragile. Use named column access (`reader.GetOrdinal("id")` or explicit column lists in SELECT) to make reads migration-safe.

- **[severity: medium] Correctness — `GetFingerprintAsync` and `GetScannedFileByPathAsync` are stub implementations that always return `null`.**
  These are cache-lookup entry points. As implemented, the cache is never consulted: every scan re-extracts every fingerprint from SolidWorks even when the file hash and extractor version are unchanged. This contradicts the spec's "fast repeated scans via caching" requirement and is the primary performance concern for large libraries. Flag as not-yet-implemented rather than a silent no-op.

- **[severity: medium] Correctness — `ApplyMigrations` executes multiple DDL statements in a single `ExecuteNonQuery` call.**
  SQLite's `Microsoft.Data.Sqlite` driver does not reliably execute batched (semicolon-separated) statements in a single command. In practice the first statement runs and the rest may be silently ignored or throw. Split each DDL statement into its own `SqliteCommand`, or use a migration loop. The fact that the application seems to work suggests the driver does handle the batch in this version, but it is not documented behavior and will fail silently on driver updates.

- **[severity: low] Correctness — `MigrateIfNeeded` does not wrap the migration SQL and the version-record INSERT in a transaction.**
  If the process dies between `apply.ExecuteNonQuery()` and `record.ExecuteNonQuery()`, the migration DDL is applied but `schema_migrations` has no record of it. The next startup will re-run the same DDL, which for `DROP INDEX IF EXISTS` followed by `CREATE UNIQUE INDEX IF NOT EXISTS` is idempotent — so it self-heals — but the absence of the version record permanently prevents any subsequent migration from detecting that v2 already ran. Wrap in a transaction.

### StaSolidWorksWorker.cs

- **[severity: medium] Error handling — `Dispose` does not wait for the STA thread to drain its queue or exit.**
  `_queue.CompleteAdding()` signals that no more items will be added, but `Dispose` returns immediately while the STA thread may still be running actions from the queue. If the caller disposes the worker and then disposes other resources that the STA thread's remaining actions depend on (e.g., `ILogger`), those actions will throw or log nothing. Call `_sta.Join()` after `_queue.CompleteAdding()`, with a reasonable timeout, and log a warning if the thread did not exit cleanly.

- **[severity: medium] Error handling — `RunAsync<T>` does not check the cancellation token, and there is no timeout.**
  If a SW COM call hangs (common when a file is corrupt or a dialog appears), the `TaskCompletionSource` will never complete and the awaiting caller will block indefinitely. Accept a `CancellationToken` in `RunAsync<T>` and register a cancellation callback that faults the `TaskCompletionSource` with `OperationCanceledException`.

- **[severity: low] Correctness — `GetOrCreateSwApp` is declared `internal` but its comment warns it must only be called from within `RunAsync`.**
  There is no enforcement; any code in the same assembly can call it from any thread. Consider making it `private` and passing the `ISldWorks` instance into the work delegate, or at minimum add a thread-affinity assertion (`Debug.Assert(Thread.CurrentThread == _sta)`).

### SolidWorksPartFingerprintExtractor.cs

- **[severity: medium] Correctness — `mp.Recalculate()` return value is ignored.**
  `IMassProperty2.Recalculate()` returns `true` on success and `false` on failure (e.g., open bodies, unsupported geometry). Ignoring the return means the extractor silently uses stale or zeroed mass/volume/surface-area values when recalculation fails. Check the return value and log a warning; treat the result as unreliable when it returns `false`.

- **[severity: low] Correctness — `ExtractBodies` casts `doc` to `IPartDoc` unconditionally.**
  `(IPartDoc)doc` will throw `InvalidCastException` if the file is an assembly or drawing that was opened inadvertently. Although the blocker targets `.SLDPRT` files, the extractor should guard this cast and return a graceful error rather than propagating the exception up to the generic catch that returns `null`.

### FileDiscoveryService.cs

- **[severity: low] Correctness — File enumeration uses the literal pattern `"*.SLDPRT"` (upper-case).**
  On Windows, `Directory.EnumerateFiles` with a literal pattern is case-insensitive, so `*.sldprt` files are found correctly. However, the pattern is documented nowhere and a developer on a case-sensitive filesystem (WSL, or a future cross-platform port) would miss lower-case files. Add a comment or use `EnumerationOptions` with `MatchCasing.CaseInsensitive` explicitly.

### ClosedXmlWorkbookExporter.cs

- **[severity: medium] Error handling — `ExportAsync` has no error handling around `wb.SaveAs(outputPath)`.**
  If `outputPath` is read-only, locked by Excel, on a full disk, or the directory does not exist, `SaveAs` throws and the exception propagates to the caller with no log message and no cleanup of the partially written file. Wrap in try/catch, log the error, and re-throw (do not swallow).

- **[severity: low] Correctness — `NeedsReview` sheet counts members with a linear `Count()` inside a `foreach` loop.**
  `ctx.Members.Count(m => m.ClusterId == cluster.Id)` in `AddNeedsReviewSheet` is O(M) per cluster, making the sheet generation O(C × M) where C is the number of clusters needing review and M is total member count. For a library with thousands of parts this is measurable. Build a `Dictionary<Guid, int>` of member counts once before the loop.

- **[severity: low] Correctness — `AddRunMetadataSheet` uses `DateTime.Now` (local time) for "Export Generated" while all other timestamps are UTC.**
  Use `DateTime.UtcNow` for consistency with the rest of the workbook.

---

## Verdict

NEEDS CHANGES — three high-severity blocking issues must be fixed before the pipeline produces reliable results:

1. `IsInCluster` transitive membership bug causes silent data loss in the DB for any cluster with 3+ members connected via intermediate nodes.
2. Missing transaction around cluster + member writes risks a torn database on any interruption.
3. Mirror detection via feature histograms alone cannot reliably prevent `ExactGeometryMatch` being assigned to mirrored part pairs; chirality (inertia-axis determinant sign) must be added as a fingerprint field to close this gap.
