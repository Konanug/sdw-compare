using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Application.Interfaces;

public sealed record BodyEquivalenceResult(
    bool Coincident,
    PartClassification Classification,
    double? TransformDeterminant,
    string Reason);

/// <summary>
/// Stage 4 — body coincidence check via SolidWorks IBody2.GetCoincidenceTransform2.
/// Distinguishes proper rigid transforms (ExactGeometryMatch) from reflections
/// (MirrorOrHandedVariant) by inspecting det(R) of the returned transform.
/// </summary>
public interface IBodyEquivalenceChecker
{
    Task<BodyEquivalenceResult> CheckAsync(
        ScannedFile fileA, string configA,
        ScannedFile fileB, string configB,
        CancellationToken ct);
}
