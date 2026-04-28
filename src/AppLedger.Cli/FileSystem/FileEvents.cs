namespace AppLedger;

internal sealed record FileEvent(
    FileEventKind Kind,
    string Path,
    long? SizeBefore,
    long? SizeAfter,
    long SizeDelta,
    DateTime? LastWriteBeforeUtc,
    DateTime? LastWriteAfterUtc,
    string Category,
    bool IsSensitive,
    string Source,
    DateTimeOffset ObservedAt,
    int? ProcessId,
    string? ProcessName,
    string? RelatedPath)
{
    public static FileEvent Created(string path, FileState current) =>
        New(FileEventKind.Created, path, null, current.Size, current.Size, null, current.LastWriteUtc, "snapshot");

    public static FileEvent Modified(string path, FileState previous, FileState current) =>
        New(FileEventKind.Modified, path, previous.Size, current.Size, current.Size - previous.Size, previous.LastWriteUtc, current.LastWriteUtc, "snapshot");

    public static FileEvent Deleted(string path, FileState previous) =>
        New(FileEventKind.Deleted, path, previous.Size, null, -previous.Size, previous.LastWriteUtc, null, "snapshot");

    public static FileEvent Live(FileEventKind kind, string path, int processId, string? processName, string? relatedPath = null) =>
        New(kind, path, null, null, 0, null, null, "etw", DateTimeOffset.Now, processId, processName, relatedPath);

    private static FileEvent New(
        FileEventKind kind,
        string path,
        long? before,
        long? after,
        long delta,
        DateTime? beforeTime,
        DateTime? afterTime,
        string source,
        DateTimeOffset? observedAt = null,
        int? processId = null,
        string? processName = null,
        string? relatedPath = null) =>
        new(
            kind,
            path,
            before,
            after,
            delta,
            beforeTime,
            afterTime,
            PathClassifier.Classify(path),
            PathClassifier.IsSensitive(path),
            source,
            observedAt ?? DateTimeOffset.Now,
            processId,
            processName,
            relatedPath);
}

internal enum FileEventKind
{
    Read,
    Created,
    Modified,
    Deleted,
    Renamed
}

internal static class FileEventMerger
{
    public static List<FileEvent> Merge(IReadOnlyList<FileEvent> liveEvents, IReadOnlyList<FileEvent> snapshotEvents)
    {
        if (liveEvents.Count == 0)
        {
            return snapshotEvents.ToList();
        }

        var merged = new List<FileEvent>(liveEvents);
        var liveWriteKeys = liveEvents
            .Where(e => e.Kind is FileEventKind.Created or FileEventKind.Modified or FileEventKind.Deleted or FileEventKind.Renamed)
            .Select(e => $"{e.Kind}|{Normalize(e.Path)}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var snapshot in snapshotEvents)
        {
            var key = $"{snapshot.Kind}|{Normalize(snapshot.Path)}";
            if (!liveWriteKeys.Contains(key))
            {
                merged.Add(snapshot);
            }
        }

        merged = NormalizeForSession(merged);

        return merged
            .OrderBy(e => e.ObservedAt)
            .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<FileEvent> NormalizeForSession(IReadOnlyList<FileEvent> events)
    {
        var result = events.ToList();
        var snapshotCreates = result
            .Where(e => e.Kind == FileEventKind.Created && e.Source.Equals("snapshot", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var snapshotCreate in snapshotCreates)
        {
            var normalizedPath = Normalize(snapshotCreate.Path);
            var liveWrites = result
                .Where(e =>
                    e.Source.Equals("etw", StringComparison.OrdinalIgnoreCase)
                    && Normalize(e.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)
                    && e.Kind == FileEventKind.Modified)
                .OrderBy(e => e.ObservedAt)
                .ToList();

            if (liveWrites.Count == 0)
            {
                continue;
            }

            foreach (var write in liveWrites)
            {
                result.Remove(write);
            }

            result.Remove(snapshotCreate);

            var firstWrite = liveWrites[0];
            result.Add(snapshotCreate with
            {
                Source = "normalized",
                ObservedAt = firstWrite.ObservedAt,
                ProcessId = firstWrite.ProcessId,
                ProcessName = firstWrite.ProcessName
            });
        }

        result = SynthesizeRenameTargets(result);

        return result
            .Select(file => file with
            {
                Category = PathClassifier.Classify(file.Kind == FileEventKind.Renamed && !string.IsNullOrWhiteSpace(file.RelatedPath) ? file.RelatedPath : file.Path),
                IsSensitive = PathClassifier.IsSensitive(file.Path) || (!string.IsNullOrWhiteSpace(file.RelatedPath) && PathClassifier.IsSensitive(file.RelatedPath))
            })
            .ToList();
    }

    private static List<FileEvent> SynthesizeRenameTargets(List<FileEvent> events)
    {
        var result = events.ToList();
        var consumedCreatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rename in result
            .Where(file => file.Kind == FileEventKind.Renamed)
            .OrderBy(file => file.ObservedAt)
            .ToList())
        {
            if (!string.IsNullOrWhiteSpace(rename.RelatedPath))
            {
                consumedCreatePaths.Add(Normalize(rename.RelatedPath));
                continue;
            }

            var sourceDirectory = NormalizeDirectory(rename.Path);
            var candidates = result
                .Where(file => file.Kind == FileEventKind.Created)
                .Where(file => file.Source.Equals("snapshot", StringComparison.OrdinalIgnoreCase)
                    || file.Source.Equals("normalized", StringComparison.OrdinalIgnoreCase))
                .Where(file => !consumedCreatePaths.Contains(Normalize(file.Path)))
                .Where(file => NormalizeDirectory(file.Path).Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase))
                .Select(file => new { File = file, NameMatch = ShareNameStem(Path.GetFileNameWithoutExtension(rename.Path), Path.GetFileNameWithoutExtension(file.Path)), Score = ScoreRenameTarget(rename, file) })
                .Where(item => item.Score > 0)
                .ToList();

            if (candidates.Count == 0)
            {
                continue;
            }

            if (candidates.Any(item => item.NameMatch))
            {
                candidates = candidates.Where(item => item.NameMatch).ToList();
            }
            else if (candidates.Count != 1)
            {
                continue;
            }

            candidates = candidates
                .OrderByDescending(item => item.Score)
                .ThenBy(item => Math.Abs((item.File.ObservedAt - rename.ObservedAt).TotalMilliseconds))
                .ThenBy(item => item.File.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var best = candidates[0];
            var secondScore = candidates.Count > 1 ? candidates[1].Score : int.MinValue;
            if (best.Score == secondScore)
            {
                continue;
            }

            consumedCreatePaths.Add(Normalize(best.File.Path));
            result.Remove(best.File);
            result.Remove(rename);
            result.Add(rename with { RelatedPath = best.File.Path });
        }

        var renameTargets = result
            .Where(file => file.Kind == FileEventKind.Renamed && !string.IsNullOrWhiteSpace(file.RelatedPath))
            .Select(file => Normalize(file.RelatedPath!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        result = result
            .Where(file => file.Kind != FileEventKind.Created || !renameTargets.Contains(Normalize(file.Path)))
            .ToList();

        return result;
    }

    private static int ScoreRenameTarget(FileEvent rename, FileEvent created)
    {
        var score = 0;
        if (rename.ProcessId is not null && rename.ProcessId == created.ProcessId)
        {
            score += 4;
        }

        var renameExtension = Path.GetExtension(rename.Path);
        var createdExtension = Path.GetExtension(created.Path);
        if (!string.IsNullOrWhiteSpace(renameExtension)
            && renameExtension.Equals(createdExtension, StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        var renameName = Path.GetFileNameWithoutExtension(rename.Path);
        var createdName = Path.GetFileNameWithoutExtension(created.Path);
        if (ShareNameStem(renameName, createdName))
        {
            score += 5;
        }

        if (created.Source.Equals("normalized", StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }
        return score;
    }

    private static bool ShareNameStem(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var leftTokens = NameTokens(left);
        var rightTokens = NameTokens(right);
        if (leftTokens.Intersect(rightTokens, StringComparer.OrdinalIgnoreCase).Any(token => token.Length >= 3))
        {
            return true;
        }

        var commonPrefix = CommonPrefixLength(left, right);
        return commonPrefix >= 5;
    }

    private static string[] NameTokens(string value)
    {
        var separators = new[] { '-', '_', '.', ' ' };
        return value
            .Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim())
            .Where(token => token.Length > 0)
            .ToArray();
    }

    private static int CommonPrefixLength(string left, string right)
    {
        var max = Math.Min(left.Length, right.Length);
        var count = 0;
        while (count < max && char.ToUpperInvariant(left[count]) == char.ToUpperInvariant(right[count]))
        {
            count++;
        }

        return count;
    }

    private static string NormalizeDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? path;
        return Normalize(directory);
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\');
}
