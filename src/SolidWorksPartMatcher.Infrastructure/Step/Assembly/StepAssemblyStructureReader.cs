using System.Text.RegularExpressions;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Assembly;

namespace SolidWorksPartMatcher.Infrastructure.Step.Assembly;

/// <summary>
/// Parses a STEP assembly file's PRODUCT / NEXT_ASSEMBLY_USAGE_OCCURRENCE structure into a flat
/// list of unique leaf parts (<see cref="AssemblyComponent"/>), each with its own geometry and
/// total instance count across the (possibly nested) assembly tree.
///
/// Reference-direction note (verified against real Autodesk-Inventor-exported CONFIG_CONTROL_DESIGN
/// STEP files — see the plan doc for the validation trace): PRODUCT_DEFINITION_FORMATION,
/// PRODUCT_DEFINITION, PRODUCT_DEFINITION_SHAPE, and SHAPE_DEFINITION_REPRESENTATION all point
/// "up" toward PRODUCT, not "down" toward the shape — so resolving PRODUCT → shape representation
/// requires composing four reverse-lookup indices built with one linear scan each, rather than a
/// naive forward walk. NEXT_ASSEMBLY_USAGE_OCCURRENCE's relating/related fields, by contrast,
/// reference PRODUCT_DEFINITION directly (a forward ref), and PRODUCT_DEFINITION → PRODUCT is
/// itself a forward walk (PD's own text contains the formation ref; formation's own text contains
/// the product ref) — no reverse index needed for that direction.
/// </summary>
public sealed class StepAssemblyStructureReader(StepP21Reader reader)
{
    private static readonly Regex ProductRx =
        new(@"^PRODUCT\('([^']*)','([^']*)'", RegexOptions.Compiled);

    // Third field only — first two fields (id, revision-or-name) may be quoted or '$'.
    private static readonly Regex FormationRx =
        new(@"^PRODUCT_DEFINITION_FORMATION\(\s*(?:'[^']*'|\$)\s*,\s*(?:'[^']*'|\$)\s*,\s*(#\d+)\s*\)", RegexOptions.Compiled);

    // Third field (formation ref) only — fourth (context) is unused.
    private static readonly Regex ProductDefinitionRx =
        new(@"^PRODUCT_DEFINITION\(\s*(?:'[^']*'|\$)\s*,\s*(?:'[^']*'|\$)\s*,\s*(#\d+)\s*,\s*#\d+\s*\)", RegexOptions.Compiled);

    // Third field (product-definition ref) only.
    private static readonly Regex ProductDefinitionShapeRx =
        new(@"^PRODUCT_DEFINITION_SHAPE\(\s*(?:'[^']*'|\$)\s*,\s*(?:'[^']*'|\$)\s*,\s*(#\d+)\s*\)", RegexOptions.Compiled);

    private static readonly Regex ShapeDefinitionRepresentationRx =
        new(@"^SHAPE_DEFINITION_REPRESENTATION\(\s*(#\d+)\s*,\s*(#\d+)\s*\)", RegexOptions.Compiled);

    // Common AP203/214 pattern: PRODUCT_DEFINITION_SHAPE's own SHAPE_REPRESENTATION carries
    // only placement (an AXIS2_PLACEMENT_3D item, no B-Rep) — the actual geometry lives in a
    // separate ADVANCED_BREP_SHAPE_REPRESENTATION, linked by this relationship entity. Verified
    // against real files: 65/83 products in the Test6 sample are wired this way; the rest have
    // their geometry directly on the SDR-referenced representation.
    private static readonly Regex ShapeRepRelationshipRx =
        new(@"^SHAPE_REPRESENTATION_RELATIONSHIP\(\s*(?:'[^']*'|\$)\s*,\s*(?:'[^']*'|\$)\s*,\s*(#\d+)\s*,\s*(#\d+)\s*\)", RegexOptions.Compiled);

    // Fourth/fifth fields (relating/related product-definition refs) — first three (id, name,
    // description) and sixth (reference designator) may be quoted or '$'.
    private static readonly Regex NauoRx = new(
        @"^NEXT_ASSEMBLY_USAGE_OCCURRENCE\(\s*(?:'[^']*'|\$)\s*,\s*(?:'[^']*'|\$)\s*,\s*(?:'[^']*'|\$)\s*,\s*(#\d+)\s*,\s*(#\d+)\s*,\s*(?:'[^']*'|\$)\s*\)",
        RegexOptions.Compiled);

    // ── Occurrence-placement chain regexes (see ResolveOccurrences) ──────────────────────────
    // CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(#transformBearingCompound, #nauoWrappingPds) — note
    // the field order is REVERSED relative to SHAPE_DEFINITION_REPRESENTATION(#pds, #shapeRep).
    private static readonly Regex CdsrRx =
        new(@"^CONTEXT_DEPENDENT_SHAPE_REPRESENTATION\(\s*(#\d+)\s*,\s*(#\d+)\s*\)", RegexOptions.Compiled);

    // Pulled out of a compound entity's raw text (which also contains REPRESENTATION_RELATIONSHIP
    // and SHAPE_REPRESENTATION_RELATIONSHIP on the same P21 instance) — just the one sub-clause
    // that carries the actual transform reference.
    private static readonly Regex RepWithTransformRx =
        new(@"REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION\s*\(\s*(#\d+)\s*\)", RegexOptions.Compiled);

    // ITEM_DEFINED_TRANSFORMATION(name, description, #sourceFrame, #targetFrame) — both frames
    // are AXIS2_PLACEMENT_3D entities; name/description are usually '$' but may be quoted.
    private static readonly Regex ItemDefinedTransformationRx = new(
        @"^ITEM_DEFINED_TRANSFORMATION\(\s*(?:'[^']*'|\$)\s*,\s*(?:'[^']*'|\$)\s*,\s*(#\d+)\s*,\s*(#\d+)\s*\)",
        RegexOptions.Compiled);

    private const int MaxNauoWalkSteps = 200_000;
    private const int MaxNauoDepth = 64;

    public AssemblyStructure Read()
    {
        var warnings = new List<string>();

        // ── Reverse-lookup indices for PRODUCT → shape-representation resolution ──────────
        var formationByProduct = BuildRefIndex("PRODUCT_DEFINITION_FORMATION", FormationRx, keyGroup: 1);
        var pdByFormation = BuildRefIndex("PRODUCT_DEFINITION", ProductDefinitionRx, keyGroup: 1);
        var pdsByPd = BuildRefIndex("PRODUCT_DEFINITION_SHAPE", ProductDefinitionShapeRx, keyGroup: 1);
        var shapeRepByPds = BuildShapeRepIndex();
        var geometryRepByShapeRep = BuildShapeRepRelationshipIndex();

        // ── PRODUCT → geometry (leaf components) ───────────────────────────────────────────
        var components = new List<AssemblyComponent>();
        var componentByProduct = new Dictionary<string, AssemblyComponent>(StringComparer.Ordinal);

        foreach (var id in reader.AllEntityIds())
        {
            if (reader.GetEntityType(id) != "PRODUCT") continue;
            if (!reader.TryGetRaw(id, out var raw)) continue;
            var m = ProductRx.Match(raw);
            if (!m.Success) continue;

            string productId = m.Groups[1].Value;
            string productName = m.Groups[2].Value;

            if (!formationByProduct.TryGetValue(id, out int formationId)) continue;
            if (!pdByFormation.TryGetValue(formationId, out int pdId)) continue;
            if (!pdsByPd.TryGetValue(pdId, out int pdsId)) continue;
            if (!shapeRepByPds.TryGetValue(pdsId, out var shapeRepInfo)) continue;
            int shapeRepId = shapeRepInfo.ShapeRepId;

            // Union the SDR-referenced representation's own closure with the closure of its
            // linked geometry representation (if a SHAPE_REPRESENTATION_RELATIONSHIP exists) —
            // covers both the indirected (placement-only SHAPE_REPRESENTATION + separate
            // ADVANCED_BREP_SHAPE_REPRESENTATION) and directly-wired cases uniformly.
            var closure = StepEntityClosureWalker.ComputeClosure(reader, shapeRepId);

            // Also seed from the SDR itself: SDR → PDS → PD → FORMATION → PRODUCT (+ their
            // context entities) are all forward refs from here, so this single seed pulls in
            // the whole upward product-identity chain a standalone snippet needs to be
            // recognized as a transferable root by STEP readers (OCCT/build123d) — without it,
            // the snippet parses cleanly but yields zero shapes, since geometry alone isn't
            // enough for readers to know it belongs to a named product.
            closure.UnionWith(StepEntityClosureWalker.ComputeClosure(reader, shapeRepInfo.SdrId));

            if (geometryRepByShapeRep.TryGetValue(shapeRepId, out var relInfo))
            {
                closure.UnionWith(StepEntityClosureWalker.ComputeClosure(reader, relInfo.GeometryRepId));
                // The SHAPE_REPRESENTATION_RELATIONSHIP entity's own id must be present too —
                // it's the only thing telling a STEP reader that shapeRepId and GeometryRepId
                // are the same part's two representations; neither side references it back
                // (the reference runs the other way), so it's never picked up by walking from
                // either endpoint and must be added explicitly.
                closure.Add(relInfo.RelationshipId);
            }

            var faces = reader.GetAdvancedFaces(closure);
            if (faces.Count == 0) continue; // pure assembly/container node — no component emitted

            // Align to the part's own principal axes before measuring anything — an embedded
            // assembly component's raw coordinates are not guaranteed to share a common canonical
            // orientation between two assembly-file revisions (see AlignToPrincipalAxes' remarks),
            // so measuring the box/volume/surface-area directly off the raw, possibly-rotated
            // coordinates would make a genuinely unchanged part look wildly different depending on
            // how it happened to be authored/embedded, and previously caused real "same part,
            // different orientation" cases to be misclassified as SuspiciousMatch/Modified.
            var rawPoints = reader.GetAllCartesianPoints(closure);
            var points = StepGeometryEstimator.AlignToPrincipalAxes(rawPoints);
            var bb = StepGeometryEstimator.ComputeSortedBoundingBox(points);
            double volumeM3 = StepGeometryEstimator.EstimateVolume(reader, faces, bb);
            double surfAreaM2 = StepGeometryEstimator.EstimateSurfaceArea(reader, faces, points, bb);

            var descriptors = new List<string>(faces.Count);
            var faceTypeHist = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (surfId, _) in faces)
            {
                var type = reader.GetEntityType(surfId);
                if (type is null) continue;
                faceTypeHist.TryGetValue(type, out int cnt);
                faceTypeHist[type] = cnt + 1;
                descriptors.Add(StepGeometryEstimator.BuildFaceDescriptor(reader, surfId, type));
            }
            descriptors.Sort(StringComparer.Ordinal);

            string matchKey = string.IsNullOrEmpty(productName) ? productId : productName;

            var component = new AssemblyComponent(
                ProductId: productId,
                ProductName: productName,
                MatchKey: matchKey,
                InstanceCount: null, // filled in by the NAUO pass below
                SortedBoundingBoxM: bb,
                VolumeM3: volumeM3,
                SurfaceAreaM2: surfAreaM2,
                FaceCount: faces.Count,
                FaceTypeHistogram: faceTypeHist,
                FaceGeometricSignature: descriptors,
                EntityClosure: closure.Order().ToList(),
                OccurrencePositionsM: []); // filled in by the occurrence pass below

            components.Add(component);
            if (!componentByProduct.TryAdd(productId, component))
                warnings.Add($"Duplicate PRODUCT id '{productId}' (entity #{id}) — multiple PRODUCT entities share this id.");
        }

        if (components.Count == 0)
        {
            warnings.Add("No leaf components with B-Rep geometry were found — file may not be a multi-part assembly.");
            return new AssemblyStructure(components, warnings);
        }

        // ── Real-volume refinement (OCCT) ───────────────────────────────────────────────────
        // Replaces the crude 55%-of-bounding-box heuristic (StepGeometryEstimator.EstimateVolume)
        // with a real CAD-kernel volume per component, computed via a minimal batch subprocess.
        // Deliberately does not touch structure/instance-count/matching/placement below — see
        // OcctVolumeRefiner's own doc comment for why. Any failure leaves the heuristic volume
        // in place for the affected component(s), with a warning; never throws.
        var realVolumes = SolidWorksPartMatcher.Infrastructure.Assembly.OcctVolumeRefiner.Refine(
            reader, components, warnings);
        if (realVolumes.Count > 0)
            for (int i = 0; i < components.Count; i++)
                if (realVolumes.TryGetValue(components[i].ProductId, out var realVolume))
                    components[i] = components[i] with { VolumeM3 = realVolume };

        // ── PRODUCT_DEFINITION → owning ProductId (forward walk: PD → FORMATION → PRODUCT) ──
        var productIdByPd = BuildProductIdByPd(warnings);

        // ── NAUO parent→child graph + instance counting ────────────────────────────────────
        var instanceCounts = ComputeInstanceCounts(productIdByPd, componentByProduct, warnings);

        // ── Per-occurrence global positions (independent walk — see ResolveOccurrences) ─────
        var occurrencePositions = ResolveOccurrences(productIdByPd, componentByProduct, warnings);

        var result = new List<AssemblyComponent>(components.Count);
        foreach (var c in components)
        {
            instanceCounts.TryGetValue(c.ProductId, out int count);
            occurrencePositions.TryGetValue(c.ProductId, out var positions);
            result.Add(c with
            {
                InstanceCount = count > 0 ? count : null,
                OccurrencePositionsM = positions ?? []
            });
        }

        return new AssemblyStructure(result, warnings);
    }

    // ── Reverse-index builders ──────────────────────────────────────────────────────────────

    // Scans every entity of `typeName`, matches `rx` against its raw text, and indexes
    // entity-id-by-referenced-id using the ref captured in `keyGroup` as the dictionary key
    // and the scanned entity's own id as the value (i.e. "which entity points at this ref").
    private Dictionary<int, int> BuildRefIndex(string typeName, Regex rx, int keyGroup)
    {
        var index = new Dictionary<int, int>();
        foreach (var id in reader.AllEntityIds())
        {
            if (reader.GetEntityType(id) != typeName) continue;
            if (!reader.TryGetRaw(id, out var raw)) continue;
            var m = rx.Match(raw);
            if (!m.Success) continue;
            if (!TryParseRef(m.Groups[keyGroup].Value, out int refId)) continue;
            index[refId] = id;
        }
        return index;
    }

    // SHAPE_DEFINITION_REPRESENTATION(#pds, #shapeRep) → index by pds id.
    // Carries both the resolved shape-representation target AND the SDR's own entity id,
    // since the SDR itself must be included in a standalone snippet's closure (see Read()).
    private Dictionary<int, (int ShapeRepId, int SdrId)> BuildShapeRepIndex()
    {
        var index = new Dictionary<int, (int, int)>();
        foreach (var id in reader.AllEntityIds())
        {
            if (reader.GetEntityType(id) != "SHAPE_DEFINITION_REPRESENTATION") continue;
            if (!reader.TryGetRaw(id, out var raw)) continue;
            var m = ShapeDefinitionRepresentationRx.Match(raw);
            if (!m.Success) continue;
            if (!TryParseRef(m.Groups[1].Value, out int pdsId)) continue;
            if (!TryParseRef(m.Groups[2].Value, out int shapeRepId)) continue;
            index[pdsId] = (shapeRepId, id);
        }
        return index;
    }

    // SHAPE_REPRESENTATION_RELATIONSHIP(name, desc, #rep1, #rep2) → index by rep1 id
    // (the placement-only representation). Carries rep2 (the geometry-bearing representation)
    // AND the relationship entity's own id — both must end up in a standalone snippet's closure.
    private Dictionary<int, (int GeometryRepId, int RelationshipId)> BuildShapeRepRelationshipIndex()
    {
        var index = new Dictionary<int, (int, int)>();
        foreach (var id in reader.AllEntityIds())
        {
            if (reader.GetEntityType(id) != "SHAPE_REPRESENTATION_RELATIONSHIP") continue;
            if (!reader.TryGetRaw(id, out var raw)) continue;
            var m = ShapeRepRelationshipRx.Match(raw);
            if (!m.Success) continue;
            if (!TryParseRef(m.Groups[1].Value, out int rep1)) continue;
            if (!TryParseRef(m.Groups[2].Value, out int rep2)) continue;
            index[rep1] = (rep2, id);
        }
        return index;
    }

    // PRODUCT_DEFINITION id → owning ProductId string, via the forward walk
    // PD --(3rd field)--> FORMATION --(3rd field)--> PRODUCT.
    private Dictionary<int, string> BuildProductIdByPd(List<string> warnings)
    {
        var result = new Dictionary<int, string>();
        foreach (var pdId in reader.AllEntityIds())
        {
            if (reader.GetEntityType(pdId) != "PRODUCT_DEFINITION") continue;
            if (!reader.TryGetRaw(pdId, out var pdRaw)) continue;
            var pdMatch = ProductDefinitionRx.Match(pdRaw);
            if (!pdMatch.Success || !TryParseRef(pdMatch.Groups[1].Value, out int formationId))
            {
                warnings.Add($"PRODUCT_DEFINITION #{pdId}: could not resolve its FORMATION reference.");
                continue;
            }

            if (!reader.TryGetRaw(formationId, out var formationRaw)) continue;
            var formationMatch = FormationRx.Match(formationRaw);
            if (!formationMatch.Success || !TryParseRef(formationMatch.Groups[1].Value, out int productId))
                continue;

            if (!reader.TryGetRaw(productId, out var productRaw)) continue;
            var productMatch = ProductRx.Match(productRaw);
            if (!productMatch.Success) continue;

            result[pdId] = productMatch.Groups[1].Value;
        }
        return result;
    }

    // Builds the NAUO parent→child PD graph, finds root PD(s), and counts root-to-leaf paths
    // per leaf ProductId (a sub-assembly used N times multiplies everything nested inside it,
    // since that's how many distinct root→...→leaf paths exist through the graph).
    private Dictionary<string, int> ComputeInstanceCounts(
        Dictionary<int, string> productIdByPd,
        Dictionary<string, AssemblyComponent> componentByProduct,
        List<string> warnings)
    {
        var childrenByParent = new Dictionary<int, List<(int ChildPd, int NauoId)>>();
        var relatingIds = new HashSet<int>();
        var relatedIds = new HashSet<int>();

        foreach (var id in reader.AllEntityIds())
        {
            if (reader.GetEntityType(id) != "NEXT_ASSEMBLY_USAGE_OCCURRENCE") continue;
            if (!reader.TryGetRaw(id, out var raw)) continue;
            var m = NauoRx.Match(raw);
            if (!m.Success) continue;
            if (!TryParseRef(m.Groups[1].Value, out int relatingPd)) continue;
            if (!TryParseRef(m.Groups[2].Value, out int relatedPd)) continue;

            relatingIds.Add(relatingPd);
            relatedIds.Add(relatedPd);
            if (!childrenByParent.TryGetValue(relatingPd, out var list))
                childrenByParent[relatingPd] = list = [];
            list.Add((relatedPd, id));
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        void RecordVisit(string productId)
        {
            counts.TryGetValue(productId, out int existing);
            counts[productId] = existing + 1;
        }

        if (childrenByParent.Count == 0)
        {
            // No assembly-structure edges at all — every leaf component is its own
            // implicit standalone occurrence.
            foreach (var productId in componentByProduct.Keys) counts[productId] = 1;
            return counts;
        }

        var roots = relatingIds.Except(relatedIds).ToList();
        int steps = 0;
        bool truncated = false;

        void Visit(int pdId, bool hasIncoming, HashSet<int> pathVisited)
        {
            if (truncated) return;
            if (++steps > MaxNauoWalkSteps || pathVisited.Count > MaxNauoDepth)
            {
                truncated = true;
                warnings.Add("NAUO instance-count walk exceeded its step/depth safety limit — some counts may be incomplete.");
                return;
            }
            if (!pathVisited.Add(pdId))
            {
                warnings.Add($"Cyclic NEXT_ASSEMBLY_USAGE_OCCURRENCE reference detected at PRODUCT_DEFINITION #{pdId} — that path was not counted.");
                return;
            }

            if (hasIncoming &&
                productIdByPd.TryGetValue(pdId, out var productId) && componentByProduct.ContainsKey(productId))
            {
                RecordVisit(productId);
            }

            if (childrenByParent.TryGetValue(pdId, out var children))
                foreach (var (childPd, _) in children)
                    Visit(childPd, true, pathVisited);

            pathVisited.Remove(pdId);
        }

        foreach (var root in roots)
            Visit(root, false, []);

        // Leaf components never reached from any root (loose parts with no NAUO edges at all)
        // still get an implicit count of 1 rather than being silently excluded.
        foreach (var productId in componentByProduct.Keys)
            if (!counts.ContainsKey(productId))
                counts[productId] = 1;

        return counts;
    }

    // Resolves the fully-composed (root-frame) 3D position of every occurrence of every geometry
    // product, via a SEPARATE walk from ComputeInstanceCounts — a deliberate duplication of the
    // NAUO parent→child graph construction so a bug here cannot affect the proven instance-count
    // walk. Returns ProductId → one position per occurrence; the occurrence count matches the
    // instance count by construction (positions are recorded on exactly the same visit condition
    // as ComputeInstanceCounts increments the count).
    //
    // Each NAUO edge's local hop transform is resolved once (see ResolveLocalHops) and composed
    // root-to-leaf through every ancestor's transform (PlacementMath.ComposeGlobal), so a part
    // that moved because a containing sub-assembly moved is captured. An unresolvable hop defaults
    // to identity — the occurrence is never dropped and nothing is thrown.
    private Dictionary<string, List<double[]>> ResolveOccurrences(
        Dictionary<int, string> productIdByPd,
        Dictionary<string, AssemblyComponent> componentByProduct,
        List<string> warnings)
    {
        var childrenByParent = new Dictionary<int, List<(int ChildPd, int NauoId)>>();
        var relatingIds = new HashSet<int>();
        var relatedIds = new HashSet<int>();
        var allNauoIds = new List<int>();

        foreach (var id in reader.AllEntityIds())
        {
            if (reader.GetEntityType(id) != "NEXT_ASSEMBLY_USAGE_OCCURRENCE") continue;
            if (!reader.TryGetRaw(id, out var raw)) continue;
            var m = NauoRx.Match(raw);
            if (!m.Success) continue;
            if (!TryParseRef(m.Groups[1].Value, out int relatingPd)) continue;
            if (!TryParseRef(m.Groups[2].Value, out int relatedPd)) continue;

            relatingIds.Add(relatingPd);
            relatedIds.Add(relatedPd);
            allNauoIds.Add(id);
            if (!childrenByParent.TryGetValue(relatingPd, out var list))
                childrenByParent[relatingPd] = list = [];
            list.Add((relatedPd, id));
        }

        var positions = new Dictionary<string, List<double[]>>(StringComparer.Ordinal);
        if (childrenByParent.Count == 0) return positions; // no structure edges → no placements

        var localHopByNauo = ResolveLocalHops(allNauoIds, warnings);

        var roots = relatingIds.Except(relatedIds).ToList();
        int steps = 0;
        bool truncated = false;

        void Visit(int pdId, AssemblyComponentPlacement globalPose, bool hasIncoming, HashSet<int> pathVisited)
        {
            if (truncated) return;
            if (++steps > MaxNauoWalkSteps || pathVisited.Count > MaxNauoDepth)
            {
                truncated = true;
                warnings.Add("NAUO occurrence-position walk exceeded its step/depth safety limit — some positions may be incomplete.");
                return;
            }
            if (!pathVisited.Add(pdId)) return; // cycle already warned about by the instance-count walk

            if (hasIncoming &&
                productIdByPd.TryGetValue(pdId, out var productId) && componentByProduct.ContainsKey(productId))
            {
                if (!positions.TryGetValue(productId, out var list))
                    positions[productId] = list = [];
                list.Add([globalPose.PositionM[0], globalPose.PositionM[1], globalPose.PositionM[2]]);
            }

            if (childrenByParent.TryGetValue(pdId, out var children))
                foreach (var (childPd, nauoId) in children)
                {
                    var hop = localHopByNauo.TryGetValue(nauoId, out var h) ? h : AssemblyComponentPlacement.Identity;
                    Visit(childPd, PlacementMath.ComposeGlobal(globalPose, hop), true, pathVisited);
                }

            pathVisited.Remove(pdId);
        }

        foreach (var root in roots)
            Visit(root, AssemblyComponentPlacement.Identity, false, []);

        return positions;
    }

    // NAUO id → its local (parent-relative) hop transform, for every given NAUO. Missing/broken
    // chains are omitted (the caller substitutes identity), with a single aggregate warning
    // reporting how many could not be resolved — the fraction is the key manual-verification
    // signal that this chain-walk generalizes past the single-instance case it was first built for.
    private Dictionary<int, AssemblyComponentPlacement> ResolveLocalHops(
        IReadOnlyList<int> nauoIds, List<string> warnings)
    {
        var result = new Dictionary<int, AssemblyComponentPlacement>();
        if (nauoIds.Count == 0) return result;
        var wanted = new HashSet<int>(nauoIds);

        // NAUO id → wrapping PRODUCT_DEFINITION_SHAPE id (a PDS whose 3rd field references a NAUO
        // rather than a PRODUCT_DEFINITION).
        var pdsByNauo = new Dictionary<int, int>();
        foreach (var id in reader.AllEntityIds())
        {
            if (reader.GetEntityType(id) != "PRODUCT_DEFINITION_SHAPE") continue;
            if (!reader.TryGetRaw(id, out var raw)) continue;
            var m = ProductDefinitionShapeRx.Match(raw);
            if (!m.Success || !TryParseRef(m.Groups[1].Value, out int refId)) continue;
            if (wanted.Contains(refId) && reader.GetEntityType(refId) == "NEXT_ASSEMBLY_USAGE_OCCURRENCE")
                pdsByNauo[refId] = id;
        }

        // Wrapping-PDS id → transform-bearing compound id (via CDSR's reversed field order).
        var compoundByPds = new Dictionary<int, int>();
        foreach (var id in reader.AllEntityIds())
        {
            if (reader.GetEntityType(id) != "CONTEXT_DEPENDENT_SHAPE_REPRESENTATION") continue;
            if (!reader.TryGetRaw(id, out var raw)) continue;
            var m = CdsrRx.Match(raw);
            if (!m.Success) continue;
            if (!TryParseRef(m.Groups[1].Value, out int compoundId)) continue;
            if (!TryParseRef(m.Groups[2].Value, out int pdsId)) continue;
            compoundByPds[pdsId] = compoundId;
        }

        int unresolved = 0;
        foreach (var nauoId in wanted)
        {
            if (TryResolveHop(nauoId, pdsByNauo, compoundByPds, out var hop))
                result[nauoId] = hop;
            else
                unresolved++;
        }
        if (unresolved > 0)
            warnings.Add($"{unresolved} of {wanted.Count} assembly occurrence transform(s) could not be resolved — " +
                         "those instances are treated as unmoved (identity) for position comparison.");

        return result;
    }

    // NAUO → (wrapping PDS) → CONTEXT_DEPENDENT_SHAPE_REPRESENTATION → transform-bearing compound
    // → ITEM_DEFINED_TRANSFORMATION → two AXIS2_PLACEMENT_3D frames → relative hop placement.
    private bool TryResolveHop(
        int nauoId, Dictionary<int, int> pdsByNauo, Dictionary<int, int> compoundByPds,
        out AssemblyComponentPlacement hop)
    {
        hop = AssemblyComponentPlacement.Identity;
        if (!pdsByNauo.TryGetValue(nauoId, out int pdsId)) return false;
        if (!compoundByPds.TryGetValue(pdsId, out int compoundId)) return false;
        if (!reader.TryGetRaw(compoundId, out var compoundRaw)) return false;

        var twm = RepWithTransformRx.Match(compoundRaw);
        if (!twm.Success || !TryParseRef(twm.Groups[1].Value, out int transformId)) return false;
        if (!reader.TryGetRaw(transformId, out var transformRaw)) return false;

        var tm = ItemDefinedTransformationRx.Match(transformRaw);
        if (!tm.Success) return false;
        if (!TryParseRef(tm.Groups[1].Value, out int frame1Id)) return false;
        if (!TryParseRef(tm.Groups[2].Value, out int frame2Id)) return false;

        if (!reader.TryGetAxisPlacement(frame1Id, out var origin1, out var axis1, out var refDir1)) return false;
        if (!reader.TryGetAxisPlacement(frame2Id, out var origin2, out var axis2, out var refDir2)) return false;

        hop = PlacementMath.ComputeRelativePlacement(origin1, axis1, refDir1, origin2, axis2, refDir2);
        return true;
    }

    private static bool TryParseRef(string token, out int id)
    {
        id = 0;
        var m = Regex.Match(token.Trim(), @"^#(\d+)$");
        return m.Success && int.TryParse(m.Groups[1].Value, out id);
    }
}
