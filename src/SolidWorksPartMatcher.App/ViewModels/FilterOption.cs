using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.App.ViewModels;

/// <summary>Combo-box item for the classification filter.</summary>
public sealed record ClassificationOption(string Label, PartClassification? Value);

/// <summary>Combo-box item for the review-status filter.</summary>
public sealed record ReviewStatusOption(string Label, ReviewStatus? Value);
