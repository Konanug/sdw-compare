namespace SolidWorksPartMatcher.Domain.Models;

public sealed record ScoringWeights(
    double BoundingBox = 0.30,
    double Volume = 0.25,
    double SurfaceArea = 0.10,
    double Topology = 0.15,
    double FeatureHistogram = 0.10,
    double MaterialProperties = 0.05,
    // Custom property values compared with fraction/decimal normalisation (2 dp).
    double CustomProperties = 0.03,
    // Filename tokens are weakest signal; weight reduced to make room for CustomProperties.
    double FilenameTokens = 0.02)
{
    public static readonly ScoringWeights Default = new();
}
