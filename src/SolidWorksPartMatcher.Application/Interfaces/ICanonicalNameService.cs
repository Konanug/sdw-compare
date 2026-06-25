using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Application.Interfaces;

public interface ICanonicalNameService
{
    string Suggest(IReadOnlyList<PartFingerprint> members, IReadOnlyList<ScannedFile> files);
}
