namespace AppLedger;

internal sealed record PathFilter(IReadOnlyList<string> Includes, IReadOnlyList<string> Excludes)
{
    public static readonly PathFilter Empty = new([], []);

    public bool HasFilters => Includes.Count > 0 || Excludes.Count > 0;

    public bool Allows(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (Excludes.Any(pattern => Matches(pattern, path)))
        {
            return false;
        }

        return Includes.Count == 0 || Includes.Any(pattern => Matches(pattern, path));
    }

    public bool AllowsAny(string? path, string? relatedPath = null) =>
        Allows(path) || (!string.IsNullOrWhiteSpace(relatedPath) && Allows(relatedPath));

    public bool ExcludesDirectory(string path) =>
        Excludes.Any(pattern => Matches(pattern, path));

    public static PathFilter From(IEnumerable<string> includes, IEnumerable<string> excludes) =>
        new(NormalizePatterns(includes), NormalizePatterns(excludes));

    private static IReadOnlyList<string> NormalizePatterns(IEnumerable<string> patterns) =>
        patterns
            .Select(Environment.ExpandEnvironmentVariables)
            .Select(pattern => pattern.Trim().Trim('"'))
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool Matches(string pattern, string path)
    {
        var normalizedPath = NormalizeForMatch(path);
        var normalizedPattern = NormalizeForMatch(pattern);

        if (normalizedPattern.Contains('*', StringComparison.Ordinal) || normalizedPattern.Contains('?', StringComparison.Ordinal))
        {
            return WildcardMatches(normalizedPattern, normalizedPath);
        }

        if (Path.IsPathRooted(normalizedPattern))
        {
            return normalizedPath.Equals(normalizedPattern, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(normalizedPattern.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        var segment = "\\" + normalizedPattern.Trim('\\') + "\\";
        return normalizedPath.Contains(segment, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.EndsWith("\\" + normalizedPattern.Trim('\\'), StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(normalizedPath).Equals(normalizedPattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool WildcardMatches(string pattern, string path)
    {
        var body = System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal);
        var regex = Path.IsPathRooted(pattern) ? "^" + body + "$" : body;
        return System.Text.RegularExpressions.Regex.IsMatch(path, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string NormalizeForMatch(string value)
    {
        value = value.Replace('/', '\\').Trim().Trim('"');
        try
        {
            if (Path.IsPathRooted(value))
            {
                value = Path.GetFullPath(value);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Keep the original pattern/path for best-effort matching.
        }

        return value.TrimEnd('\\');
    }
}
