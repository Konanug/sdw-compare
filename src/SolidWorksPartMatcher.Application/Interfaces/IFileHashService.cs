namespace SolidWorksPartMatcher.Application.Interfaces;

public interface IFileHashService
{
    Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken);
}
