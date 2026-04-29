namespace AppLedger;

internal static class CleanupPlanner
{
    public static CleanupPlan Build(SessionReport session)
    {
        var candidates = session.TopFolders
            .Where(folder => folder.BytesAdded > 0)
            .Where(folder => !IsNeverCleanupCandidate(folder.Path))
            .Select(Classify)
            .Where(candidate => candidate is not null)
            .Cast<CleanupCandidate>()
            .GroupBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => candidate.Bucket).First())
            .OrderByDescending(candidate => candidate.BytesAdded)
            .ThenByDescending(candidate => candidate.FileCount)
            .Take(30)
            .ToList();

        return new CleanupPlan(
            candidates.Where(candidate => candidate.Bucket == CleanupBucket.Safe).Take(15).ToList(),
            candidates.Where(candidate => candidate.Bucket == CleanupBucket.Review).Take(15).ToList());
    }

    private static CleanupCandidate? Classify(FolderImpact folder)
    {
        var normalized = folder.Path.Replace('/', '\\');
        if (folder.Category == "temp" || HasSafeCacheSegment(normalized))
        {
            return new CleanupCandidate(
                CleanupBucket.Safe,
                folder.Path,
                folder.Category,
                folder.FileCount,
                folder.BytesAdded,
                "Likely temporary/cache data. Review while the app is closed before deleting.");
        }

        if (folder.Category == "app-data" || normalized.Contains("\\AppData\\", StringComparison.OrdinalIgnoreCase))
        {
            return new CleanupCandidate(
                CleanupBucket.Review,
                folder.Path,
                folder.Category,
                folder.FileCount,
                folder.BytesAdded,
                "Application data. May include settings, login state, or workspace data.");
        }

        return null;
    }

    private static bool HasSafeCacheSegment(string normalized) =>
        normalized.Contains("\\Cache\\", StringComparison.OrdinalIgnoreCase)
        || normalized.Contains("\\Caches\\", StringComparison.OrdinalIgnoreCase)
        || normalized.Contains("\\Code Cache\\", StringComparison.OrdinalIgnoreCase)
        || normalized.Contains("\\GPUCache\\", StringComparison.OrdinalIgnoreCase)
        || normalized.Contains("\\Cache_Data\\", StringComparison.OrdinalIgnoreCase)
        || normalized.Contains("\\DawnCache\\", StringComparison.OrdinalIgnoreCase)
        || normalized.Contains("\\ShaderCache\\", StringComparison.OrdinalIgnoreCase)
        || normalized.Contains("\\GrShaderCache\\", StringComparison.OrdinalIgnoreCase)
        || normalized.Contains("\\Crashpad\\", StringComparison.OrdinalIgnoreCase)
        || normalized.Contains("\\Temp\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsNeverCleanupCandidate(string path)
    {
        var normalized = path.Replace('/', '\\');
        return PathClassifier.IsSystemRuntimeNoise(path)
            || normalized.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\node_modules\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\Documents\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\Desktop\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\Downloads\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\.ssh\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\.aws\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\.azure\\", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record CleanupPlan(
    IReadOnlyList<CleanupCandidate> Safe,
    IReadOnlyList<CleanupCandidate> Review)
{
    public long SafeBytes => Safe.Sum(candidate => candidate.BytesAdded);

    public long ReviewBytes => Review.Sum(candidate => candidate.BytesAdded);
}

internal sealed record CleanupCandidate(
    CleanupBucket Bucket,
    string Path,
    string Category,
    int FileCount,
    long BytesAdded,
    string Reason);

internal enum CleanupBucket
{
    Review,
    Safe
}
