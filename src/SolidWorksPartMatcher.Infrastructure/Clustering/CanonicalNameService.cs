using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Infrastructure.Clustering;

public sealed class CanonicalNameService : ICanonicalNameService
{
    private int _counter;

    public string Suggest(IReadOnlyList<PartFingerprint> members, IReadOnlyList<ScannedFile> files)
    {
        if (members.Count == 0 && files.Count == 0)
            return NextFallback();

        // Prefer actual file names; config names like "Default" are meaningless for naming
        var names = files.Count > 0
            ? files.Select(f => Path.GetFileNameWithoutExtension(f.FileName)).ToList()
            : members
                .Select(m => m.ConfigName)
                .Where(n => !string.Equals(n, "Default", StringComparison.OrdinalIgnoreCase) && n.Length > 0)
                .Select(n => Path.GetFileNameWithoutExtension(n))
                .ToList();

        if (names.Count == 0)
            return NextFallback();
        var prefix = LongestCommonPrefix(names);

        if (prefix.Length >= 4)
            return Sanitize(prefix);

        // Try longest shared token
        var tokenSets = names.Select(Tokenize).ToList();
        var common = tokenSets[0];
        foreach (var ts in tokenSets.Skip(1))
            common = common.Intersect(ts, StringComparer.OrdinalIgnoreCase).ToHashSet();

        if (common.Count > 0)
            return Sanitize(string.Join("-", common.OrderBy(t => t)));

        return NextFallback();
    }

    private string NextFallback() => $"PART-{Interlocked.Increment(ref _counter):D6}";

    private static string LongestCommonPrefix(IReadOnlyList<string> strings)
    {
        if (strings.Count == 0) return string.Empty;
        var first = strings[0];
        var len = first.Length;
        for (var i = 1; i < strings.Count; i++)
        {
            var s = strings[i];
            len = Math.Min(len, s.Length);
            for (var j = 0; j < len; j++)
            {
                if (char.ToUpperInvariant(first[j]) != char.ToUpperInvariant(s[j]))
                {
                    len = j;
                    break;
                }
            }
        }
        return first[..len].TrimEnd(['-', '_', ' ', '.']);
    }

    private static HashSet<string> Tokenize(string s)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tok in s.Split([' ', '_', '-', '.'], StringSplitOptions.RemoveEmptyEntries))
            if (tok.Length > 2) result.Add(tok);
        return result;
    }

    private static string Sanitize(string s)
    {
        var clean = new System.Text.StringBuilder();
        foreach (var c in s)
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                clean.Append(c);
        return clean.Length == 0 ? "PART" : clean.ToString().ToUpperInvariant();
    }
}
