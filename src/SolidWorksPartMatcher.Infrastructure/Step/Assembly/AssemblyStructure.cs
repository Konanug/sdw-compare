using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Infrastructure.Step.Assembly;

/// <summary>Result of parsing one STEP assembly file's PRODUCT/NAUO structure.</summary>
public sealed record AssemblyStructure(
    IReadOnlyList<AssemblyComponent> Components,
    IReadOnlyList<string> Warnings);
