using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Application.Interfaces;

public interface IAssemblyDiffReportExporter
{
    Task ExportAsync(AssemblyDiffSummary summary, string outputPath, CancellationToken ct);
}
