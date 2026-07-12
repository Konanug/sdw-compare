using Microsoft.Extensions.Logging;
using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Step;


namespace SolidWorksPartMatcher.Infrastructure.Orchestration;

public sealed class ScanOrchestrationService(
    IFileDiscoveryService discovery,
    IFileHashService hasher,
    IPartFingerprintExtractor extractor,
    ICandidateBlocker blocker,
    ICandidateScorer scorer,
    IClusterBuilder clusterBuilder,
    ICanonicalNameService nameService,
    IPartRepository repo,
    ILogger<ScanOrchestrationService> logger,
    IBodyEquivalenceChecker? bodyChecker = null,
    IDetailedGeometryComparator? geometryComparator = null,
    ITessellationComparator? tessellationComparator = null,
    StepGeometryExtractor? stepExtractor = null) : IScanOrchestrationService
{
    private const double CandidateThreshold = 0.40;

    public async Task<ScanRun> RunScanAsync(
        IReadOnlyList<string> rootPaths,
        ScoringWeights? weights,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var w = weights ?? ScoringWeights.Default;
        var appVersion = typeof(ScanOrchestrationService).Assembly.GetName().Version?.ToString() ?? "0.0";

        var run = await repo.CreateScanRunAsync(new ScanRun(
            Id: Guid.NewGuid(),
            StartedUtc: DateTime.UtcNow,
            EndedUtc: null,
            SourceRoots: rootPaths,
            AppVersion: appVersion,
            Status: ScanStatus.Running,
            ScanSettingsJson: null), cancellationToken);

        try
        {
            // Stage 0: File discovery
            Report(progress, "Discovery", "Scanning folders…", 0, 0);
            var files = new List<ScannedFile>();
            await foreach (var file in discovery.DiscoverAsync(rootPaths, null, cancellationToken))
            {
                files.Add(file);
                Report(progress, "Discovery", file.FileName, files.Count, 0);
            }
            logger.LogInformation("Discovered {Count} part files (SLDPRT + STEP)", files.Count);

            // Stage 0b: Hashing (parallel, pure .NET)
            Report(progress, "Hashing", $"Hashing {files.Count} files…", 0, files.Count);
            var hashed = new ScannedFile[files.Count];
            var hashLock = new object();
            var hashDone = 0;

            await Parallel.ForEachAsync(
                files.Select((f, i) => (f, i)),
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
                async (item, ct) =>
                {
                    var (file, idx) = item;
                    if (file.Status == FileStatus.Failed)
                    {
                        hashed[idx] = file;
                        return;
                    }
                    try
                    {
                        var sha = await hasher.ComputeSha256Async(file.NormalizedPath, ct);
                        hashed[idx] = file with { Sha256 = sha, Status = FileStatus.Hashed };
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Hash failed: {Path}", file.NormalizedPath);
                        hashed[idx] = file with { Status = FileStatus.Failed, Error = ex.Message };
                    }
                    var done = Interlocked.Increment(ref hashDone);
                    Report(progress, "Hashing", file.FileName, done, files.Count);
                });

            // Persist files with correct scan run id
            foreach (var f in hashed)
                await repo.UpsertScannedFileAsync(f, run.Id, cancellationToken);

            // Binary duplicate groups — open SolidWorks (or fake) once per unique SHA
            var bySha = hashed
                .Where(f => f.Sha256 != null && f.Status != FileStatus.Failed)
                .GroupBy(f => f.Sha256!)
                .ToDictionary(g => g.Key, g => g.ToList());

            logger.LogInformation("{Unique} unique SHA-256 hashes, {Dup} binary duplicate groups",
                bySha.Count, bySha.Values.Count(v => v.Count > 1));

            // Stage 1: Extract geometry template once per unique SHA-256, then stamp
            //          one fingerprint record per file so the blocker sees all copies.
            Report(progress, "Fingerprinting", "Extracting geometry…", 0, bySha.Count);
            var templateBySha = new Dictionary<string, PartFingerprint>();
            var shasDone = 0;

            int cacheHits = 0;

            // Pass 1 — cache resolution. Cache key is (SHA, config, extractor version): a hit
            // reuses geometry from a prior scan without re-opening the file (the dominant cost for
            // SLDPRT via SolidWorks, and now avoids re-running OCCT for STEP volume). Safe because
            // identical bytes + same extractor version deterministically produce the same
            // fingerprint; a version bump misses and re-extracts. Reused templates' ids are
            // remapped per-file below, exactly as freshly-extracted ones are.
            var misses = new List<(string Sha, ScannedFile File, bool IsStep)>();
            foreach (var (sha, group) in bySha)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = group[0];
                bool isStep = IsStepFile(file.NormalizedPath);
                int extractorVersion = (isStep && stepExtractor != null)
                    ? StepGeometryExtractor.Version
                    : extractor.ExtractorVersion;

                var cached = await repo.GetFingerprintAsync(sha, "Default", extractorVersion, cancellationToken);

                // A cached STEP fingerprint whose geometry was only ESTIMATED (OCCT was unavailable
                // when it was extracted) is treated as a miss. The cache key does not encode whether
                // the kernel was available, so without this a machine that first scanned STEP before
                // Python/OCCT was installed would serve estimate-based geometry from cache forever —
                // and engraving detection, which refuses to run on estimated geometry, would silently
                // never fire. Re-extracting is a cheap pure-C# P21 parse; if OCCT is still missing we
                // simply land on the estimate again.
                bool cachedButEstimated = cached != null && isStep && cached.GeometrySource != "occt";

                if (cached != null && !cachedButEstimated)
                {
                    templateBySha[sha] = cached;
                    cacheHits++;
                    Report(progress, "Fingerprinting", file.FileName, ++shasDone, bySha.Count);
                }
                else
                {
                    if (cachedButEstimated)
                        logger.LogInformation(
                            "Re-extracting {File}: cached STEP geometry was estimated, not kernel-measured",
                            file.FileName);
                    misses.Add((sha, file, isStep));
                }
            }

            // Pass 2 — real OCCT geometry for STEP misses, in one batched subprocess. Replaces the
            // crude P21 estimates with real CAD-kernel values: the volume, the surface area, and a
            // tight rotation-invariant oriented bounding box. Degrades to the estimates (empty map)
            // when the OCCT tool is absent.
            var realStepGeomByPath =
                new Dictionary<string, Assembly.OcctVolumeRefiner.OcctMeasurement>(StringComparer.Ordinal);
            var stepMissPaths = misses
                .Where(m => m.IsStep && stepExtractor != null)
                .Select(m => m.File.NormalizedPath)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (stepMissPaths.Count > 0)
                realStepGeomByPath = await StepPartVolumeRefiner.RefineAsync(
                    stepMissPaths,
                    msg => logger.LogInformation("STEP geometry: {Msg}", msg),
                    cancellationToken);

            // Pass 3 — extract the misses.
            foreach (var (sha, file, isStep) in misses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    PartFingerprint? template;
                    if (isStep && stepExtractor != null)
                    {
                        // Pure C# path: parse P21 directly — no SolidWorks COM needed for STEP.
                        template = stepExtractor.Extract(file);

                        // Override the P21 estimates with the real OCCT measurements when available.
                        // All three must come from the same kernel: the estimated volume (0.55 ×
                        // bbVolume) and surface area (the box formula) are pure functions of the
                        // bounding box, so mixing a real volume with an estimated box would leave two
                        // of the three numbers derived from the third. GeometrySource records which
                        // way it went — downstream (StepEngravingDetector) refuses to compare
                        // fine-grained deltas on estimated geometry. Same accept-guards as the
                        // assembly path (StepAssemblyStructureReader).
                        if (template != null
                            && realStepGeomByPath.TryGetValue(file.NormalizedPath, out var m))
                        {
                            template = template with { VolumeM3 = m.VolumeM3, GeometrySource = "occt" };
                            if (m.BboxM is { Length: 3 } bbox && bbox.All(d => d > 0))
                                template = template with { SortedBoundingBoxM = bbox };
                            if (m.AreaM2 is { } area && area > 0)
                                template = template with { SurfaceAreaM2 = area };
                        }
                    }
                    else if (isStep)
                    {
                        logger.LogWarning("No StepGeometryExtractor registered — skipping STEP file {File}", file.FileName);
                        template = null;
                    }
                    else
                    {
                        // SLDPRT: existing SolidWorks COM path — completely unchanged.
                        template = await extractor.ExtractAsync(file, "Default", cancellationToken);
                    }

                    if (template != null)
                        templateBySha[sha] = template;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Fingerprint extraction threw for {File} — skipping file", file.FileName);
                }
                Report(progress, "Fingerprinting", file.FileName, ++shasDone, bySha.Count);
            }

            // Create one fingerprint record per file (reuse template geometry for binary dups)
            var fingerprints = new List<PartFingerprint>();
            foreach (var file in hashed.Where(f => f.Sha256 != null && f.Status != FileStatus.Failed))
            {
                if (!templateBySha.TryGetValue(file.Sha256!, out var template)) continue;
                // Give each file its own unique fingerprint ID keyed on file.Id
                var fp = template with { Id = PerFileGuid(file.Id), ScannedFileId = file.Id };
                fingerprints.Add(fp);
                await repo.UpsertFingerprintAsync(fp, cancellationToken);
            }

            logger.LogInformation("Stored {Count} fingerprints ({Unique} unique geometries, {Hits} reused from cache)",
                fingerprints.Count, templateBySha.Count, cacheHits);

            // Stage 2: Candidate blocking
            Report(progress, "Blocking", "Generating candidates…", 0, 0);
            var rawCandidates = blocker.GenerateCandidates(fingerprints);
            logger.LogInformation("Candidate pairs: {Count}", rawCandidates.Count);

            // Stage 3: Scoring and classification
            Report(progress, "Scoring", $"Scoring {rawCandidates.Count} candidates…", 0, rawCandidates.Count);
            var fpById = fingerprints.ToDictionary(f => f.Id);
            var fileByScannedId = hashed.ToDictionary(f => f.Id);
            var pairs = new List<CandidatePair>();
            var scoreDone = 0;

            foreach (var (aId, bId, buckets) in rawCandidates)
            {
                if (!fpById.TryGetValue(aId, out var fpA) || !fpById.TryGetValue(bId, out var fpB))
                    continue;

                var score = scorer.Score(fpA, fpB, w);
                if (score < CandidateThreshold) { scoreDone++; continue; }

                var isBinaryDup = fpA.FileSha256 == fpB.FileSha256;
                var (isMirror, mirrorReason) = ClassifyMirror(fpA, fpB, score);

                // Resolve the ScannedFile references up-front so classification reasons can name the
                // specific file that carries a Hole Wizard hole or an engraving, instead of "A"/"B".
                fileByScannedId.TryGetValue(fpA.ScannedFileId, out var sfA);
                fileByScannedId.TryGetValue(fpB.ScannedFileId, out var sfB);
                string nameA = sfA?.FileName ?? "A";
                string nameB = sfB?.FileName ?? "B";

                // Hole Wizard vs plain cut-extrude is a different engineering specification, so such a
                // pair must never auto-merge. Unlike before we no longer short-circuit to Distinct
                // here: the geometry stages are allowed to run so we can tell the user whether the
                // holes actually sit in the same positions. The verdict is applied after Stage 5.
                var (holeConflict, aUsesWizard) = HoleSpecConflict(fpA, fpB);

                // Everything up to (but not including) the SolidWorks COM stages is pure geometry.
                // Extracted so the classification ladder — and in particular the two guards on Stage
                // 3.5, each of which fixes a real bug — can be regression-tested without a DI graph,
                // a SolidWorks install, or a display. See ClassifyGeometrySignals.
                var verdict = ClassifyGeometrySignals(
                    fpA, fpB, score, nameA, nameB, isBinaryDup, isMirror, mirrorReason);

                PartClassification cls = verdict.Cls;
                string reason = verdict.Reason;
                string comparatorVersion = verdict.ComparatorVersion;

                // Whether either file is a STEP — SW COM stages must be skipped for these.
                bool hasAnyStep = fpA.SourceFormat == "STEP" || fpB.SourceFormat == "STEP";

                if (verdict.Engraving is { IsEngraving: true } hit)
                    logger.LogInformation("Stage 3.7 engraving variant {A}↔{B}: {Reason}",
                        sfA?.FileName, sfB?.FileName, hit.Reason);
                else if (verdict.Engraving is { NearMiss: true } nearMiss)
                    // Only NEAR misses — pairs that genuinely share a box and a volume and differ only
                    // in face count, yet failed a fine-grained gate. Logging every rejection would mean
                    // a line for every unrelated STEP pair in the scan and would drown the signal.
                    // Information, not Debug, because the file logger is capped there — and this line is
                    // the whole calibration mechanism for MaxCurvedFractionOfAddedFaces, the one
                    // threshold shipped as a guess rather than a measurement.
                    logger.LogInformation("Stage 3.7 engraving candidate rejected {A}↔{B}: {Reason}",
                        sfA?.FileName, sfB?.FileName, nearMiss.Reason);

                // Stage 4: body coincidence — definitive proper-rotation vs reflection test.
                // Runs for high-score non-binary SLDPRT-SLDPRT pairs to confirm ExactGeometryMatch
                // or correctly classify mirrors. det(R)≈+1 → exact; det(R)≈-1 → mirror.
                // Skipped whenever either file is STEP (SW COM cannot reliably open STEP silently).
                // Note: a hole-specification conflict no longer blocks this stage. We deliberately run
                // it so we can establish whether the two bodies actually coincide — i.e. whether the
                // holes are in the same positions — before reporting the conflict. The hole-spec
                // verdict below overrides whatever this stage concludes, so it can never auto-merge.
                if (bodyChecker != null && sfA != null && sfB != null && !isBinaryDup && !isMirror
                    && cls != PartClassification.EngravingVariant
                    && !hasAnyStep && score >= 0.85)
                {
                    var bodyResult = await WithComRetryAsync(
                        () => bodyChecker.CheckAsync(sfA, fpA.ConfigName, sfB, fpB.ConfigName, cancellationToken),
                        $"Stage 4 body check {sfA.FileName}↔{sfB.FileName}", cancellationToken);

                    if (bodyResult != null && bodyResult.Classification != PartClassification.ComparisonFailed)
                    {
                        cls = bodyResult.Classification;
                        reason = bodyResult.Reason;
                        comparatorVersion = "body-coincidence-1";
                        logger.LogDebug("Stage 4 result for {A}↔{B}: {Cls} (det={D})",
                            sfA.FileName, sfB.FileName, cls, bodyResult.TransformDeterminant);
                    }
                }

                // Stage 4.5: tessellation tolerance comparison.
                // Runs when Stage 4 produced PossibleMatch or Distinct (i.e. GetCoincidenceTransform2
                // returned "not coincident"), and the pair still has sufficient coarse similarity.
                // Handles parts that differ by ≤0.5 mm manufacturing tolerance and parts where
                // one side has text-engraving cut extrudes (SketchTextCutCount differs).
                bool stage4Inconclusive = cls == PartClassification.PossibleMatch
                    || cls == PartClassification.Distinct;
                if (tessellationComparator != null && sfA != null && sfB != null
                    && !isBinaryDup && !isMirror && !holeConflict
                    && cls != PartClassification.EngravingVariant
                    && !hasAnyStep && stage4Inconclusive && score >= 0.75)
                {
                    const double toleranceM = 0.0005; // 0.5 mm
                    var tessResult = await WithComRetryAsync(
                        () => tessellationComparator.CompareAsync(sfA, fpA, sfB, fpB, toleranceM, cancellationToken),
                        $"Stage 4.5 tessellation {sfA.FileName}↔{sfB.FileName}", cancellationToken);

                    if (tessResult != null && tessResult.Classification != PartClassification.ComparisonFailed)
                    {
                        cls = tessResult.Classification;
                        reason = tessResult.Reason;
                        comparatorVersion = "tessellation-hd95-1";
                        logger.LogDebug("Stage 4.5 result for {A}↔{B}: {Cls}",
                            sfA.FileName, sfB.FileName, cls);
                    }
                }

                // Material variant reclassification: if geometry confirms an exact match but
                // materials differ, reclassify so Excel can note the discrepancy.
                // Applies after Stage 4 or Stage 4.5 produce ExactGeometryMatch.
                if (cls == PartClassification.ExactGeometryMatch)
                {
                    cls = ReclassifyMaterialVariant(fpA, fpB, cls, ref reason);
                }

                // Stage 5: volumetric Jaccard via boolean intersection (IBody2.Operations2).
                // Runs on PossibleMatch pairs with sufficient coarse score to detect RevisionFamily
                // (same design intent, slightly different dimensions). Does NOT run when Stage 4/4.5
                // already produced a definitive result (ExactGeometryMatch, Mirror, Engraving).
                if (geometryComparator != null && sfA != null && sfB != null
                    && cls == PartClassification.PossibleMatch && !isBinaryDup
                    && !holeConflict && !hasAnyStep && score >= 0.70)
                {
                    var detail = await WithComRetryAsync(
                        () => geometryComparator.CompareAsync(sfA, fpA, sfB, fpB, cancellationToken),
                        $"Stage 5 volumetric {sfA.FileName}↔{sfB.FileName}", cancellationToken);

                    if (detail != null && detail.Classification != PartClassification.ComparisonFailed)
                    {
                        cls = detail.Classification;
                        reason = detail.Reason;
                        comparatorVersion = "volumetric-jaccard-1";
                        logger.LogDebug("Stage 5 result for {A}↔{B}: {Cls} (J={J:F3})",
                            sfA.FileName, sfB.FileName, cls, detail.JaccardSimilarity);
                    }
                }

                // Hole-specification verdict — applied last so it overrides every geometry stage and
                // can never auto-merge. A Hole Wizard hole and a plain cut-extrude are different
                // engineering specifications even when the solids coincide.
                //   • Shapes coincide  → the holes sit in the same positions, but were modelled
                //     differently. Surface it as PossibleMatch (grouped, NeedsReview) naming which
                //     file uses which, rather than hiding it as Distinct like the old behaviour did.
                //   • Shapes differ    → they are simply different parts; stay Distinct.
                if (holeConflict && !isBinaryDup)
                {
                    string wizardFile = aUsesWizard ? nameA : nameB;
                    string plainFile = aUsesWizard ? nameB : nameA;
                    bool sameShape = cls is PartClassification.ExactGeometryMatch
                                         or PartClassification.GeometryMatchMetadataVariant;

                    if (sameShape)
                    {
                        cls = PartClassification.PossibleMatch;
                        reason =
                            $"Same shape — the holes are in the same positions — but the hole specification " +
                            $"differs: '{wizardFile}' uses a Hole Wizard hole, '{plainFile}' uses a plain " +
                            $"cut extrude. Different engineering specification, so not merged automatically.";
                    }
                    else
                    {
                        cls = PartClassification.Distinct;
                        reason =
                            $"Hole specification differs ('{wizardFile}' uses a Hole Wizard hole, " +
                            $"'{plainFile}' uses a plain cut extrude) and the shapes are not identical; " +
                            $"coarse score {score:F2}.";
                    }
                    comparatorVersion = "hole-spec-2";
                    logger.LogDebug("Hole-spec verdict for {A}↔{B}: {Cls}", nameA, nameB, cls);
                }

                var pair = new CandidatePair(
                    Id: Guid.NewGuid(),
                    ScanRunId: run.Id,
                    FingerprintAId: aId,
                    FingerprintBId: bId,
                    CoarseScore: score,
                    MatchedBuckets: buckets,
                    Classification: cls,
                    Confidence: score,
                    ClassificationReason: reason,
                    ComparatorVersion: comparatorVersion,
                    ToleranceProfile: "default");

                pairs.Add(pair);
                await repo.UpsertCandidatePairAsync(pair, cancellationToken);
                Report(progress, "Scoring", $"{fpA.ConfigName} ↔ {fpB.ConfigName}", ++scoreDone, rawCandidates.Count);
            }

            // Stage 4: Clustering
            Report(progress, "Clustering", "Building clusters…", 0, 0);
            var clusters = clusterBuilder.BuildClusters(run.Id, fingerprints, pairs, hashed, nameService);

            // Build fp→cluster map via the same union-find so transitively-joined members
            // are correctly assigned even when not directly compared to the cluster root.
            var fpToCluster = BuildFingerprintToClusterMap(fingerprints, pairs, clusters);

            foreach (var cluster in clusters)
            {
                var rep = cluster.RepresentativeFingerprintId;
                var members = fingerprints
                    .Where(f => fpToCluster.TryGetValue(f.Id, out var cid) && cid == cluster.Id)
                    .Select(f => new ClusterMember(cluster.Id, f.Id, f.Id == rep))
                    .ToList();
                await repo.UpsertClusterWithMembersAsync(cluster, members, cancellationToken);
            }

            logger.LogInformation("Created {Count} clusters", clusters.Count);

            await repo.UpdateScanRunStatusAsync(run.Id, ScanStatus.Completed, DateTime.UtcNow, cancellationToken);
            Report(progress, "Done", $"Scan complete. {clusters.Count} part groups found.", clusters.Count, clusters.Count);
            return run with { Status = ScanStatus.Completed, EndedUtc = DateTime.UtcNow };
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Scan cancelled");
            await repo.UpdateScanRunStatusAsync(run.Id, ScanStatus.Cancelled, DateTime.UtcNow, CancellationToken.None);
            return run with { Status = ScanStatus.Cancelled };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scan failed");
            await repo.UpdateScanRunStatusAsync(run.Id, ScanStatus.Failed, DateTime.UtcNow, CancellationToken.None);
            return run with { Status = ScanStatus.Failed };
        }
    }

    // Pre-screens for mirror pairs using fingerprint-level signals only (no extra SW opens).
    // Stage 4 (BodyEquivalenceChecker) provides the definitive transform-determinant check.
    internal static (bool IsMirror, string? Reason) ClassifyMirror(
        PartFingerprint a, PartFingerprint b, double score)
    {
        // 1. Chirality sign from principal inertia axes.
        //    NOTE: SW may normalize eigenvectors to a right-handed frame, making this +1 for
        //    all parts. When it works it's definitive; when it doesn't, we fall through.
        if (a.ChiralitySign.HasValue && b.ChiralitySign.HasValue
            && a.ChiralitySign.Value != 0 && b.ChiralitySign.Value != 0)
        {
            if (Math.Sign(a.ChiralitySign.Value) != Math.Sign(b.ChiralitySign.Value))
                return (true, $"Opposite chirality (principal-axis det: {a.ChiralitySign:+0.##;-0.##} vs {b.ChiralitySign:+0.##;-0.##}); coarse score {score:F2}");

            return (false, null); // same sign — definitively not a mirror at fingerprint level
        }

        // 2. CoM-in-BB offset: the center of mass sits in a different position within the
        //    bounding box for a mirrored part. For mirror across axis k: ratio_A[k] + ratio_B[k] ≈ 1.
        //    Only fires when CoM is meaningfully off-center (avoids false positives on symmetric parts).
        var comResult = CheckCoMOffsetMirror(a, b);
        if (comResult.IsMirror)
            return (true, $"CoM-in-BB offset indicates mirror (axis {comResult.Axis} reflected, ratio {comResult.RatioA:F3}↔{comResult.RatioB:F3}); score {score:F2}");

        // 3. Feature histogram heuristic: one part has a mirror-body SW feature, the other doesn't.
        //    Validated SW feature type names (GetTypeName2, SW 2019+):
        //      "MirrorSolid" / "Mirror" / "BodyMirror"
        var mirrorA = HasMirrorFeature(a.FeatureTypeHistogram);
        var mirrorB = HasMirrorFeature(b.FeatureTypeHistogram);
        if (mirrorA != mirrorB && score >= 0.70)
        {
            var which = mirrorA ? "A" : "B";
            return (true, $"Mirror feature in file {which} (feature-histogram heuristic); coarse score {score:F2}");
        }

        return (false, null);
    }

    private static (bool IsMirror, int Axis, double RatioA, double RatioB)
        CheckCoMOffsetMirror(PartFingerprint a, PartFingerprint b, double tol = 0.05)
    {
        var ra = a.CoMOffsetInBB;
        var rb = b.CoMOffsetInBB;
        if (ra == null || rb == null || ra.Length < 3 || rb.Length < 3)
            return (false, -1, 0, 0);

        // BB dimensions must match (same physical part, just mirrored).
        var bbA = a.SortedBoundingBoxM;
        var bbB = b.SortedBoundingBoxM;
        for (int i = 0; i < 3; i++)
        {
            double max = Math.Max(bbA[i], bbB[i]);
            if (max > 1e-10 && Math.Abs(bbA[i] - bbB[i]) / max > tol)
                return (false, -1, 0, 0);
        }

        // For each sorted-BB axis, check if ratio_A + ratio_B ≈ 1 (reflected) while
        // the other axes have ratio_A ≈ ratio_B (matching).
        for (int mirrorAxis = 0; mirrorAxis < 3; mirrorAxis++)
        {
            // Skip if CoM is too close to the BB midpoint on this axis — symmetric mass
            // distribution means we can't confidently say it's mirrored vs. coincidentally 0.5.
            if (Math.Abs(ra[mirrorAxis] - 0.5) < 0.03) continue;

            bool match = true;
            for (int ax = 0; ax < 3; ax++)
            {
                if (ax == mirrorAxis)
                { if (Math.Abs(ra[ax] + rb[ax] - 1.0) > tol) { match = false; break; } }
                else
                { if (Math.Abs(ra[ax] - rb[ax]) > tol) { match = false; break; } }
            }
            if (match)
                return (true, mirrorAxis, ra[mirrorAxis], rb[mirrorAxis]);
        }
        return (false, -1, 0, 0);
    }

    // Re-runs union-find on exact-match edges so every fingerprint maps to its cluster root.
    // This correctly handles transitive membership (A=B, B=C → A,B,C all map to same root)
    // without relying on O(N×M) linear search.
    private static Dictionary<Guid, Guid> BuildFingerprintToClusterMap(
        IReadOnlyList<PartFingerprint> fingerprints,
        IReadOnlyList<CandidatePair> pairs,
        IReadOnlyList<PartCluster> clusters)
    {
        var parent = fingerprints.ToDictionary(f => f.Id, f => f.Id);
        var rank = fingerprints.ToDictionary(f => f.Id, _ => 0);

        Guid Find(Guid id)
        {
            while (parent[id] != id) { parent[id] = parent[parent[id]]; id = parent[id]; }
            return id;
        }

        void Union(Guid a, Guid b)
        {
            var ra = Find(a); var rb = Find(b);
            if (ra == rb) return;
            if (rank[ra] < rank[rb]) (ra, rb) = (rb, ra);
            parent[rb] = ra;
            if (rank[ra] == rank[rb]) rank[ra]++;
        }

        foreach (var p in pairs)
            if (p.Classification.IsMatch())
                Union(p.FingerprintAId, p.FingerprintBId);

        // Cluster IDs are the union-find roots chosen by UnionFindClusterBuilder.
        var clusterIds = clusters.Select(c => c.Id).ToHashSet();
        return fingerprints.ToDictionary(
            f => f.Id,
            f =>
            {
                var root = Find(f.Id);
                // If the root matches a known cluster id, use it; otherwise the fp is its own singleton cluster.
                return clusterIds.Contains(root) ? root : f.Id;
            });
    }

    // Known SW feature type names that indicate a mirror/body-mirror operation.
    internal static readonly HashSet<string> MirrorFeatureTypes =
        new(StringComparer.OrdinalIgnoreCase) { "MirrorSolid", "Mirror", "BodyMirror" };

    // A feature is a mirror indicator if it is one of the canonical names above OR its type name
    // simply contains "mirror" (case-insensitive). The substring catch-all makes recognition
    // consistent across SW mirror-feature variants whose exact GetTypeName2 string we haven't
    // enumerated (e.g. "MirrorPattern", "MirrorComponent", "ImportedMirror"): a feature literally
    // named with "mirror" is a mirror operation, and ordinary features (Extrude/Cut/Fillet/…) never
    // contain the word — so this widens true-positive coverage without adding false positives.
    // This is why one mirrored part could be missed while similar ones were caught: its mirror
    // feature carried a name outside the fixed three-item set, so the histogram heuristic never
    // fired and the pair fell through to the geometric stages (where a near-symmetric part can
    // legitimately read as an exact match).
    internal static bool HasMirrorFeature(IReadOnlyDictionary<string, int> histogram)
        => histogram.Keys.Any(k =>
            MirrorFeatureTypes.Contains(k)
            || k.Contains("mirror", StringComparison.OrdinalIgnoreCase));

    internal static (bool IsEngravingVariant, string? Reason)
        DetectEngravingVariantBySuppressedGeometry(PartFingerprint a, PartFingerprint b)
    {
        if (a.SuppressedVolumeM3 == null && b.SuppressedVolumeM3 == null) return (false, null);

        double baseVolA = a.SuppressedVolumeM3 ?? a.VolumeM3;
        double baseVolB = b.SuppressedVolumeM3 ?? b.VolumeM3;
        int baseFaceA = a.SuppressedFaceCount ?? a.FaceCount;
        int baseFaceB = b.SuppressedFaceCount ?? b.FaceCount;
        int baseEdgeA = a.SuppressedEdgeCount ?? a.EdgeCount;
        int baseEdgeB = b.SuppressedEdgeCount ?? b.EdgeCount;
        int baseVertA = a.SuppressedVertexCount ?? a.VertexCount;
        int baseVertB = b.SuppressedVertexCount ?? b.VertexCount;

        if (baseFaceA != baseFaceB || baseEdgeA != baseEdgeB || baseVertA != baseVertB)
            return (false, null);

        double maxVol = Math.Max(Math.Abs(baseVolA), Math.Abs(baseVolB));
        if (maxVol > 1e-12 && Math.Abs(baseVolA - baseVolB) / maxVol > 0.001)
            return (false, null);

        var baseBBA = a.SuppressedBoundingBoxM ?? a.SortedBoundingBoxM;
        var baseBBB = b.SuppressedBoundingBoxM ?? b.SortedBoundingBoxM;
        for (int i = 0; i < 3; i++)
            if (Math.Abs(baseBBA[i] - baseBBB[i]) > 0.0005) return (false, null);

        return (true,
            $"Same base geometry confirmed by suppression: " +
            $"engravings A={a.SketchTextCutCount} B={b.SketchTextCutCount}; " +
            $"base vol={baseVolA * 1e6:F3} cm³ faces={baseFaceA}");
    }

    // After geometry is confirmed as ExactGeometryMatch, reclassify to
    // GeometryMatchMetadataVariant when the material specification differs.
    // The pair is still auto-merged in clustering; the difference is noted in Excel.
    internal static PartClassification ReclassifyMaterialVariant(
        PartFingerprint a, PartFingerprint b,
        PartClassification current, ref string reason)
    {
        if (current != PartClassification.ExactGeometryMatch) return current;
        if (string.IsNullOrEmpty(a.Material) && string.IsNullOrEmpty(b.Material)) return current;
        if (string.Equals(a.Material, b.Material, StringComparison.OrdinalIgnoreCase)) return current;

        reason += $"; material difference: A={a.Material ?? "(none)"} B={b.Material ?? "(none)"}";
        return PartClassification.GeometryMatchMetadataVariant;
    }

    /// <summary>
    /// A Hole Wizard hole and a plain cut-extrude are different engineering specifications, so a
    /// pair where exactly one side uses the Hole Wizard must never auto-merge. Returns whether the
    /// conflict exists and — so the report can name the file rather than say "A"/"B" — whether it is
    /// <paramref name="a"/> that carries the Hole Wizard feature.
    /// </summary>
    internal static (bool Conflict, bool AUsesWizard) HoleSpecConflict(PartFingerprint a, PartFingerprint b)
    {
        bool wa = PartFeatureFacts.HasHoleWizard(a);
        bool wb = PartFeatureFacts.HasHoleWizard(b);
        return (wa != wb, wa);
    }

    /// <summary>
    /// The verdict of the pure-geometry portion of the classification ladder, plus the engraving
    /// detector's own result so the caller can log a hit or a near miss without re-running it.
    /// </summary>
    internal sealed record GeometryVerdict(
        PartClassification Cls,
        string Reason,
        string ComparatorVersion,
        StepEngravingDetector.Result? Engraving = null);

    /// <summary>
    /// The classification ladder up to (but excluding) the SolidWorks COM stages: the initial verdict,
    /// then Stage 3.5 (face signature), 3.6 (STEP evidence vote) and 3.7 (STEP engraving). Pure and
    /// synchronous — no COM, no I/O, no DI — so the ladder's ordering and its guards are directly
    /// testable. Stages 4/4.5/5, the material-variant reclassification and the hole-spec verdict stay
    /// in <see cref="RunScanAsync"/>, since they need the COM comparators.
    /// </summary>
    internal static GeometryVerdict ClassifyGeometrySignals(
        PartFingerprint fpA, PartFingerprint fpB, double score,
        string nameA, string nameB,
        bool isBinaryDup, bool isMirror, string? mirrorReason,
        StepMatchTolerances? stepTol = null,
        StepEngravingTolerances? engravingTol = null)
    {
        stepTol ??= StepMatchTolerances.Default;
        engravingTol ??= StepEngravingTolerances.Default;

        PartClassification cls;
        string reason;
        string comparatorVersion = "coarse-1";

        if (isBinaryDup)
        {
            cls = PartClassification.BinaryDuplicate;
            reason = "SHA-256 match";
        }
        else if (isMirror)
        {
            cls = PartClassification.MirrorOrHandedVariant;
            reason = mirrorReason!;
        }
        else
        {
            // Primary: suppression-based comparison (requires suppression to succeed).
            var (isSuppressedEngraving, suppEngReason) =
                DetectEngravingVariantBySuppressedGeometry(fpA, fpB);
            if (isSuppressedEngraving)
            {
                cls = PartClassification.EngravingVariant;
                reason = suppEngReason!;
            }
            // Fallback: suppression failed or wasn't attempted, but the SketchTextCutCount
            // field tells us one part has a text-sketch feature and the other doesn't.
            // A text engraving (CutExtrusion) removes material and adds faces, which lowers
            // the coarse score — so use a lower threshold than ExactGeometryMatch.
            else if ((fpA.SketchTextCutCount > 0) != (fpB.SketchTextCutCount > 0) && score >= 0.60)
            {
                cls = PartClassification.EngravingVariant;
                var (engFile, engCount, plainFile) = fpA.SketchTextCutCount > 0
                    ? (nameA, fpA.SketchTextCutCount, nameB)
                    : (nameB, fpB.SketchTextCutCount, nameA);
                reason =
                    $"Engraving differs: '{engFile}' has {engCount} engraved text feature(s), " +
                    $"'{plainFile}' has none; coarse score {score:F2}";
            }
            else if (score >= 0.90)
            {
                cls = PartClassification.ExactGeometryMatch;
                reason = $"Coarse score {score:F2}";
            }
            else
            {
                cls = PartClassification.PossibleMatch;
                reason = $"Coarse score {score:F2}";
            }
        }

        // Stage 3.5: face geometric signature comparison.
        // Runs whenever both fingerprints carry a FaceGeometricSignature (STEP from the P21 parser,
        // SLDPRT from the SW COM face enumeration). Exact sorted-descriptor match → ExactGeometryMatch;
        // same face count but differing parameters → PossibleMatch; different face count → Distinct.
        // For SLDPRT-SLDPRT pairs Stage 4 may still upgrade/downgrade this result.
        //
        // The !isMirror and EngravingVariant guards mirror Stages 4 and 4.5, and are NOT optional —
        // without them this stage silently destroyed both verdicts. A face descriptor encodes surface
        // type/axis/radius but NOT position, and CanonicalizeAxis additionally erases the axis SIGN:
        //   • Mirror — a part chiral only by hole PLACEMENT yields byte-identical sorted descriptors,
        //     so this stage returned ExactGeometryMatch. Stages 4/4.5 are gated !isMirror and so never
        //     ran to correct it, and a left/right-handed pair was presented as a CONFIRMED match — a
        //     false automatic merge, the one thing this tool must never do.
        //   • Engraving — an engraved part has a different face count, so this stage returned Distinct,
        //     overwriting the EngravingVariant the suppression detector had just established and
        //     hiding the pair from the UI entirely (Distinct groups are filtered out).
        if (!isBinaryDup && !isMirror
            && cls != PartClassification.EngravingVariant
            && fpA.FaceGeometricSignature != null && fpB.FaceGeometricSignature != null)
        {
            var (sigCls, sigReason) = CompareStepFaceSignatures(fpA, fpB, score);
            cls = sigCls;
            reason = sigReason;
            comparatorVersion = "face-sig-1";
        }

        // Stage 3.6: STEP-only geometric-evidence vote. When the exact face-signature match above
        // leaves a STEP-STEP pair as PossibleMatch or Distinct (e.g. radii differ by export noise),
        // count orientation-invariant signals — real volume, face count, face-type histogram, tolerant
        // signature. If enough agree, escalate to PossibleMatch so a human reviews it, with the
        // agreeing signals spelled out in the reason. It only raises review recall: never a
        // confirmed/auto-merged match, never a downgrade of an exact/binary/mirror result. STEP has no
        // SW stages (4/4.5/5) to provide a tolerance net, so this is that net.
        bool bothStep = fpA.SourceFormat == "STEP" && fpB.SourceFormat == "STEP";
        if (bothStep && !isBinaryDup && !isMirror
            && (cls == PartClassification.PossibleMatch || cls == PartClassification.Distinct))
        {
            var vote = StepGeometryEvidenceVote.Evaluate(fpA, fpB, stepTol);
            if (vote.Escalate)
            {
                cls = PartClassification.PossibleMatch;
                reason = vote.Reason;
                comparatorVersion = "step-evidence-vote-1";
            }
        }

        // Stage 3.7: STEP engraving variant. STEP has no feature tree, so both SLDPRT engraving checks
        // above are structurally dead for it — and an engraving adds hundreds of faces, which Stage 3.5
        // reads as Distinct and Stage 3.6's vote structurally cannot rescue (three of its four flags
        // are face-count-sensitive, so an engraved pair can raise at most one). The engraved twin
        // therefore vanishes from the UI. Recognise it from geometry instead: same box, volume barely
        // moved, far more faces, and every one of the base part's faces still present.
        //
        // Runs after 3.6 so that when the vote has already escalated Distinct → PossibleMatch, this
        // refines it to the more specific label. Both are IsMatch() edges landing in a NeedsReview
        // cluster, so this never confirms and never auto-merges — it only makes an invisible pair
        // visible, correctly named.
        StepEngravingDetector.Result? engraving = null;
        if (bothStep && !isBinaryDup && !isMirror
            && (cls == PartClassification.Distinct || cls == PartClassification.PossibleMatch))
        {
            engraving = StepEngravingDetector.Detect(fpA, fpB, engravingTol);
            if (engraving.IsEngraving)
            {
                cls = PartClassification.EngravingVariant;
                reason = $"{engraving.Reason}; coarse score {score:F2}";
                comparatorVersion = "step-engraving-1";
            }
        }

        return new GeometryVerdict(cls, reason, comparatorVersion, engraving);
    }

    // Compares sorted FaceGeometricSignature lists for a STEP-STEP pair.
    // Exact match → ExactGeometryMatch. Same face count but descriptors differ → PossibleMatch.
    // Different face count → Distinct (different topology).
    internal static (PartClassification Classification, string Reason) CompareStepFaceSignatures(
        PartFingerprint a, PartFingerprint b, double coarseScore)
    {
        var sigA = a.FaceGeometricSignature!;
        var sigB = b.FaceGeometricSignature!;

        if (sigA.Count != sigB.Count)
            return (PartClassification.Distinct,
                $"Different face count (A={sigA.Count} B={sigB.Count}); coarse {coarseScore:F2}");

        bool allMatch = true;
        for (int i = 0; i < sigA.Count; i++)
        {
            if (!string.Equals(sigA[i], sigB[i], StringComparison.Ordinal))
            {
                allMatch = false;
                break;
            }
        }

        if (allMatch)
            return (PartClassification.ExactGeometryMatch,
                $"STEP face signatures match exactly ({sigA.Count} faces); coarse {coarseScore:F2}");

        return (PartClassification.PossibleMatch,
            $"Same face count ({sigA.Count}) but descriptors differ; coarse {coarseScore:F2}");
    }

    private static bool IsStepFile(string path)
    {
        var ext = Path.GetExtension(path);
        return string.Equals(ext, ".step", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".stp", StringComparison.OrdinalIgnoreCase);
    }

    // Bounded retry for the SolidWorks-COM comparison stages (4 / 4.5 / 5). A transient COM
    // failure would otherwise be caught and silently drop the pair back to its coarse
    // classification, which is a source of run-to-run nondeterminism: a match confirmed in one
    // scan can disappear in the next purely because a COM call happened to throw. Retrying once
    // before giving up makes that far less likely without changing the result on success or the
    // safe fallback on genuine failure (null → caller keeps the prior classification, exactly as
    // the previous catch block did). Cancellation is never retried.
    private async Task<T?> WithComRetryAsync<T>(Func<Task<T>> op, string label, CancellationToken ct)
        where T : class
    {
        const int maxAttempts = 2;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await op();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt >= maxAttempts)
                {
                    logger.LogWarning(ex, "{Label} failed after {Attempts} attempts — keeping prior classification",
                        label, maxAttempts);
                    return null;
                }
                logger.LogWarning(ex, "{Label} attempt {Attempt}/{Max} threw — retrying", label, attempt, maxAttempts);
            }
        }
        return null;
    }

    private static void Report(IProgress<ScanProgress>? p, string stage, string detail, int cur, int total)
        => p?.Report(new ScanProgress(stage, detail, cur, total));

    private static Guid PerFileGuid(Guid fileId)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(fileId.ToByteArray());
        return new Guid(bytes);
    }
}
