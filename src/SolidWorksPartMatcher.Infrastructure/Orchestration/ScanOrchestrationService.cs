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

            foreach (var (sha, group) in bySha)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var file = group[0];
                    bool isStep = IsStepFile(file.NormalizedPath);
                    PartFingerprint? template;

                    if (isStep && stepExtractor != null)
                    {
                        // Pure C# path: parse P21 directly — no SolidWorks COM needed for STEP.
                        template = stepExtractor.Extract(file);
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
                    logger.LogError(ex, "Fingerprint extraction threw for {File} — skipping file",
                        group[0].FileName);
                }
                Report(progress, "Fingerprinting", group[0].FileName, ++shasDone, bySha.Count);
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

            logger.LogInformation("Stored {Count} fingerprints ({Unique} unique geometries)",
                fingerprints.Count, templateBySha.Count);

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
                // Hole Wizard vs plain cut-extrude is a different engineering specification
                // regardless of geometric coincidence — block all geometry stages for this pair.
                var hasHoleWizardConflict = HasHoleWizardConflict(fpA, fpB);

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
                else if (hasHoleWizardConflict)
                {
                    cls = PartClassification.Distinct;
                    reason = $"Hole specification conflict (one uses Hole Wizard, other uses plain cut); score {score:F2}";
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
                        reason = $"Text sketch on one part (A={fpA.SketchTextCutCount} B={fpB.SketchTextCutCount}); coarse score={score:F2}";
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

                // Look up ScannedFile references once — needed by both Stage 4 and Stage 5.
                fileByScannedId.TryGetValue(fpA.ScannedFileId, out var sfA);
                fileByScannedId.TryGetValue(fpB.ScannedFileId, out var sfB);

                // Whether either file is a STEP — SW COM stages must be skipped for these.
                bool hasAnyStep = fpA.SourceFormat == "STEP" || fpB.SourceFormat == "STEP";

                // Stage 3.5: face geometric signature comparison.
                // Runs whenever both fingerprints carry a FaceGeometricSignature (STEP from P21
                // parser, SLDPRT from SW COM face enumeration).  Exact sorted-descriptor match →
                // ExactGeometryMatch; same face count but differing parameters → PossibleMatch.
                // For SLDPRT-SLDPRT pairs Stage 4 may still upgrade/downgrade this result.
                if (!isBinaryDup
                    && fpA.FaceGeometricSignature != null && fpB.FaceGeometricSignature != null)
                {
                    var (sigCls, sigReason) = CompareStepFaceSignatures(fpA, fpB, score);
                    cls = sigCls;
                    reason = sigReason;
                    comparatorVersion = "face-sig-1";
                    logger.LogDebug("Stage 3.5 face-sig result for {A}↔{B}: {Cls}",
                        sfA?.FileName, sfB?.FileName, cls);
                }

                // Stage 4: body coincidence — definitive proper-rotation vs reflection test.
                // Runs for high-score non-binary SLDPRT-SLDPRT pairs to confirm ExactGeometryMatch
                // or correctly classify mirrors. det(R)≈+1 → exact; det(R)≈-1 → mirror.
                // Skipped whenever either file is STEP (SW COM cannot reliably open STEP silently).
                if (bodyChecker != null && sfA != null && sfB != null && !isBinaryDup && !isMirror
                    && !hasHoleWizardConflict && cls != PartClassification.EngravingVariant
                    && !hasAnyStep && score >= 0.85)
                {
                    try
                    {
                        var bodyResult = await bodyChecker.CheckAsync(
                            sfA, fpA.ConfigName, sfB, fpB.ConfigName, cancellationToken);

                        if (bodyResult.Classification != PartClassification.ComparisonFailed)
                        {
                            cls = bodyResult.Classification;
                            reason = bodyResult.Reason;
                            comparatorVersion = "body-coincidence-1";
                            logger.LogDebug("Stage 4 result for {A}↔{B}: {Cls} (det={D})",
                                sfA.FileName, sfB.FileName, cls, bodyResult.TransformDeterminant);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Stage 4 body check threw for {A}↔{B} — keeping coarse classification",
                            sfA.FileName, sfB.FileName);
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
                    && !isBinaryDup && !isMirror && !hasHoleWizardConflict
                    && cls != PartClassification.EngravingVariant
                    && !hasAnyStep && stage4Inconclusive && score >= 0.75)
                {
                    try
                    {
                        const double toleranceM = 0.0005; // 0.5 mm
                        var tessResult = await tessellationComparator.CompareAsync(
                            sfA, fpA, sfB, fpB, toleranceM, cancellationToken);

                        if (tessResult.Classification != PartClassification.ComparisonFailed)
                        {
                            cls = tessResult.Classification;
                            reason = tessResult.Reason;
                            comparatorVersion = "tessellation-hd95-1";
                            logger.LogDebug("Stage 4.5 result for {A}↔{B}: {Cls}",
                                sfA.FileName, sfB.FileName, cls);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "Stage 4.5 tessellation comparison threw for {A}↔{B} — keeping prior classification",
                            sfA.FileName, sfB.FileName);
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
                    && !hasHoleWizardConflict && !hasAnyStep && score >= 0.70)
                {
                    try
                    {
                        var detail = await geometryComparator.CompareAsync(
                            sfA, fpA, sfB, fpB, cancellationToken);

                        if (detail.Classification != PartClassification.ComparisonFailed)
                        {
                            cls = detail.Classification;
                            reason = detail.Reason;
                            comparatorVersion = "volumetric-jaccard-1";
                            logger.LogDebug("Stage 5 result for {A}↔{B}: {Cls} (J={J:F3})",
                                sfA.FileName, sfB.FileName, cls, detail.JaccardSimilarity);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "Stage 5 volumetric comparison threw for {A}↔{B} — keeping prior classification",
                            sfA.FileName, sfB.FileName);
                    }
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
            if (p.Classification is PartClassification.BinaryDuplicate
                                 or PartClassification.ExactGeometryMatch
                                 or PartClassification.GeometryMatchMetadataVariant
                                 or PartClassification.EngravingVariant
                                 or PartClassification.RevisionFamily
                                 or PartClassification.MirrorOrHandedVariant
                                 or PartClassification.PossibleMatch)
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

    internal static bool HasMirrorFeature(IReadOnlyDictionary<string, int> histogram)
        => histogram.Keys.Any(k => MirrorFeatureTypes.Contains(k));

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

    // Quality.17: Hole Wizard and a plain cut-extrude are different parts.
    // Returns true when one fingerprint has HoleWzd features and the other does not.
    private static bool HasHoleWizardConflict(PartFingerprint a, PartFingerprint b)
    {
        static bool HasWzd(PartFingerprint fp) =>
            fp.FeatureTypeHistogram.Keys.Any(k =>
                k.StartsWith("HoleWzd", StringComparison.OrdinalIgnoreCase));
        return HasWzd(a) != HasWzd(b);
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

    private static void Report(IProgress<ScanProgress>? p, string stage, string detail, int cur, int total)
        => p?.Report(new ScanProgress(stage, detail, cur, total));

    private static Guid PerFileGuid(Guid fileId)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(fileId.ToByteArray());
        return new Guid(bytes);
    }
}
