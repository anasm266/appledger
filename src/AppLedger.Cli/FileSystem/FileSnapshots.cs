namespace AppLedger;

internal sealed record FileSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<string> Roots,
    Dictionary<string, FileState> Files,
    IReadOnlyList<string> Errors)
{
    public static FileSnapshot Capture(IReadOnlyList<string> roots)
        => Capture(roots, PathFilter.Empty);

    public static FileSnapshot Capture(IReadOnlyList<string> roots, PathFilter pathFilter)
    {
        var files = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        var errors = new ConcurrentBag<string>();

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in SafeEnumerateFiles(root, errors, pathFilter))
            {
                try
                {
                    if (!pathFilter.Allows(file))
                    {
                        continue;
                    }

                    var info = new FileInfo(file);
                    files[info.FullName] = new FileState(info.Length, info.LastWriteTimeUtc);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                {
                    errors.Add($"{file}: {ex.Message}");
                }
            }
        }

        return new FileSnapshot(DateTimeOffset.Now, roots, files, errors.ToArray());
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, ConcurrentBag<string> errors, PathFilter pathFilter)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> directories;
            IEnumerable<string> files;

            try
            {
                directories = Directory.EnumerateDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                errors.Add($"{current}: {ex.Message}");
                continue;
            }

            foreach (var directory in directories)
            {
                if (!PathClassifier.IsUsuallyNoise(directory) && !pathFilter.ExcludesDirectory(directory))
                {
                    pending.Push(directory);
                }
            }

            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                errors.Add($"{current}: {ex.Message}");
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }
}

internal sealed record FileState(long Size, DateTime LastWriteUtc);

internal static class FileDiff
{
    public static List<FileEvent> Compare(FileSnapshot before, FileSnapshot after)
    {
        var events = new List<FileEvent>();

        foreach (var (path, current) in after.Files)
        {
            if (!before.Files.TryGetValue(path, out var previous))
            {
                events.Add(FileEvent.Created(path, current));
                continue;
            }

            if (current.Size != previous.Size || current.LastWriteUtc != previous.LastWriteUtc)
            {
                events.Add(FileEvent.Modified(path, previous, current));
            }
        }

        foreach (var (path, previous) in before.Files)
        {
            if (!after.Files.ContainsKey(path))
            {
                events.Add(FileEvent.Deleted(path, previous));
            }
        }

        return events
            .OrderByDescending(e => e.Kind == FileEventKind.Created || e.Kind == FileEventKind.Modified ? Math.Abs(e.SizeDelta) : 0)
            .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
