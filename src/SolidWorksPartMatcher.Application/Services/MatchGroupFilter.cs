using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Application.Services;

/// <summary>
/// Pure static helpers for match-group display and filtering.
/// Keeps the logic separately testable from WPF view-model code.
/// </summary>
public static class MatchGroupFilter
{
    /// <summary>Human-readable label for a classification.</summary>
    public static string ToLabel(PartClassification cls) => cls switch
    {
        PartClassification.BinaryDuplicate               => "Geometry Match (Identical Copy)",
        PartClassification.ExactGeometryMatch            => "Geometry Match",
        PartClassification.GeometryMatchMetadataVariant  => "Geometry Match (Metadata Variant)",
        PartClassification.MirrorOrHandedVariant         => "Geometry Match (Mirror Variant)",
        PartClassification.RevisionFamily                => "Geometry Match (Revision Family)",
        PartClassification.EngravingVariant              => "Geometry Match (Engraving Variant)",
        PartClassification.PossibleMatch                 => "Possible Match",
        PartClassification.Distinct                      => "Distinct",
        PartClassification.ComparisonFailed              => "Comparison Failed",
        _                                                => cls.ToString()
    };

    /// <summary>
    /// Returns true when a group passes the active search and filter criteria.
    /// A group is visible when:
    ///   — the classification filter matches (or is null = "All"), AND
    ///   — the review-status filter matches (or is null = "All"), AND
    ///   — the search text (if any) appears in the canonical name, any child filename,
    ///     or any child full path.
    /// </summary>
    public static bool MatchesFilter(
        string? canonicalName,
        PartClassification classification,
        ReviewStatus reviewStatus,
        IEnumerable<(string FileName, string FullPath)> files,
        string? searchText,
        PartClassification? classificationFilter,
        ReviewStatus? reviewStatusFilter)
    {
        if (classificationFilter.HasValue && classification != classificationFilter.Value)
            return false;

        if (reviewStatusFilter.HasValue)
        {
            // Pending and NeedsReview are treated as the same bucket for filtering.
            if (reviewStatusFilter.Value == ReviewStatus.Pending)
            {
                if (reviewStatus != ReviewStatus.Pending && reviewStatus != ReviewStatus.NeedsReview)
                    return false;
            }
            else if (reviewStatus != reviewStatusFilter.Value)
                return false;
        }

        if (string.IsNullOrWhiteSpace(searchText))
            return true;

        var search = searchText.Trim();

        if (canonicalName?.Contains(search, StringComparison.OrdinalIgnoreCase) == true)
            return true;

        foreach (var (fileName, fullPath) in files)
        {
            if (fileName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                fullPath.Contains(search, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Builds deterministic display names ("Match 1", "Match 2", …) for a
    /// sorted list of clusters.  Sort by canonical name for stable ordering.
    /// </summary>
    public static IReadOnlyList<string> BuildDisplayNames(
        IEnumerable<PartCluster> clusters)
    {
        var sorted = clusters
            .OrderBy(c => c.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Id)
            .ToList();

        var names = new string[sorted.Count];
        for (int i = 0; i < sorted.Count; i++)
            names[i] = $"Match {i + 1}";
        return names;
    }
}
