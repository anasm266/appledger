namespace AppLedger;

internal static class PersistenceAnalyzer
{
    public static PersistenceSummary Build(IReadOnlyList<FileEvent> files, IReadOnlyList<RegistryEvent> registry)
    {
        var items = new List<PersistenceItem>();
        items.AddRange(BuildStartupFolderItems(files));
        items.AddRange(registry.Select(BuildRegistryItem).Where(item => item is not null).Cast<PersistenceItem>());

        var ordered = items
            .GroupBy(item => $"{item.Kind}|{item.Name}|{item.Action}|{item.Detail}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();

        return new PersistenceSummary(
            ordered,
            ordered.Count(item => item.Kind is "startup-registry" or "startup-folder"),
            ordered.Count(item => item.Kind == "service"),
            ordered.Count(item => item.Kind == "scheduled-task"),
            ordered.Count(item => item.Kind == "protocol-handler"),
            ordered.Count(item => item.Kind == "file-association"));
    }

    public static IReadOnlyList<Finding> BuildFindings(PersistenceSummary summary)
    {
        var findings = new List<Finding>();
        AddFindingGroup(findings, summary.Items, "startup-registry", "high", "Startup persistence change");
        AddFindingGroup(findings, summary.Items, "startup-folder", "high", "Startup folder persistence change");
        AddFindingGroup(findings, summary.Items, "service", "high", "Windows service persistence change");
        AddFindingGroup(findings, summary.Items, "scheduled-task", "high", "Scheduled task persistence change");
        AddFindingGroup(findings, summary.Items, "protocol-handler", "medium", "Protocol handler change");
        AddFindingGroup(findings, summary.Items, "file-association", "medium", "File association change");
        return findings;
    }

    private static IEnumerable<PersistenceItem> BuildStartupFolderItems(IReadOnlyList<FileEvent> files)
    {
        return files
            .Where(file => file.Kind is FileEventKind.Created or FileEventKind.Modified or FileEventKind.Deleted or FileEventKind.Renamed)
            .Where(file => IsStartupFolderPath(file.Path) || (!string.IsNullOrWhiteSpace(file.RelatedPath) && IsStartupFolderPath(file.RelatedPath)))
            .GroupBy(file => NormalizePath(file.RelatedPath ?? file.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.OrderBy(file => file.ObservedAt).First();
                var path = first.RelatedPath ?? first.Path;
                return new PersistenceItem(
                    "startup-folder",
                    "high",
                    first.Kind.ToString(),
                    Path.GetFileName(path),
                    path,
                    null,
                    null,
                    Rank: 10);
            });
    }

    private static PersistenceItem? BuildRegistryItem(RegistryEvent entry)
    {
        var key = entry.Key.Replace('/', '\\');
        if (IsStartupRegistry(key))
        {
            return new PersistenceItem(
                "startup-registry",
                "high",
                entry.Kind.ToString(),
                LastSegment(key),
                $"{entry.Kind}: {key}",
                entry.Before,
                entry.After,
                Rank: 20);
        }

        if (TryServiceItem(entry, key, out var service))
        {
            return service;
        }

        if (TryScheduledTaskItem(entry, key, out var task))
        {
            return task;
        }

        if (IsProtocolHandler(key))
        {
            return new PersistenceItem(
                "protocol-handler",
                "medium",
                entry.Kind.ToString(),
                ExtractProtocolName(key),
                $"{entry.Kind}: {key}",
                entry.Before,
                entry.After,
                Rank: 50);
        }

        if (IsFileAssociation(key))
        {
            return new PersistenceItem(
                "file-association",
                "medium",
                entry.Kind.ToString(),
                ExtractFileAssociationName(key),
                $"{entry.Kind}: {key}",
                entry.Before,
                entry.After,
                Rank: 60);
        }

        return null;
    }

    private static bool TryServiceItem(RegistryEvent entry, string key, out PersistenceItem? item)
    {
        item = null;
        const string marker = "\\SYSTEM\\CurrentControlSet\\Services\\";
        var markerIndex = key.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var relative = key[(markerIndex + marker.Length)..];
        var parts = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var serviceName = parts[0];
        var valueName = parts.Length > 1 ? parts[^1] : "";
        var command = valueName.Equals("ImagePath", StringComparison.OrdinalIgnoreCase)
            ? entry.After ?? entry.Before
            : null;
        var startMode = valueName.Equals("Start", StringComparison.OrdinalIgnoreCase)
            ? DescribeServiceStartMode(entry.After ?? entry.Before)
            : null;
        var serviceType = valueName.Equals("Type", StringComparison.OrdinalIgnoreCase)
            ? DescribeServiceType(entry.After ?? entry.Before)
            : null;
        var detail = command is not null
            ? $"{entry.Kind}: {serviceName} image path = {command}"
            : startMode is not null
                ? $"{entry.Kind}: {serviceName} start mode = {startMode}"
                : serviceType is not null
                    ? $"{entry.Kind}: {serviceName} type = {serviceType}"
                    : $"{entry.Kind}: {serviceName} {valueName}".TrimEnd();

        item = new PersistenceItem(
            "service",
            "high",
            entry.Kind.ToString(),
            serviceName,
            detail,
            entry.Before,
            entry.After,
            Rank: 30);
        return true;
    }

    private static bool TryScheduledTaskItem(RegistryEvent entry, string key, out PersistenceItem? item)
    {
        item = null;
        const string treeMarker = "\\Schedule\\TaskCache\\Tree\\";
        const string fileMarker = "\\ScheduledTasks\\";
        var treeIndex = key.IndexOf(treeMarker, StringComparison.OrdinalIgnoreCase);
        var fileIndex = key.IndexOf(fileMarker, StringComparison.OrdinalIgnoreCase);
        if (treeIndex < 0 && fileIndex < 0)
        {
            return false;
        }

        var relative = treeIndex >= 0
            ? key[(treeIndex + treeMarker.Length)..]
            : key[(fileIndex + fileMarker.Length)..];
        var parts = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var valueName = parts.Length > 1 ? parts[^1] : "";
        var taskName = string.Join("\\", valueName.Length == 0 ? parts : parts.Take(parts.Length - 1));
        var action = valueName.StartsWith("Command", StringComparison.OrdinalIgnoreCase)
            ? entry.After ?? entry.Before
            : null;
        var arguments = valueName.StartsWith("Arguments", StringComparison.OrdinalIgnoreCase)
            ? entry.After ?? entry.Before
            : null;
        var workingDirectory = valueName.StartsWith("WorkingDirectory", StringComparison.OrdinalIgnoreCase)
            ? entry.After ?? entry.Before
            : null;
        var triggerSummary = valueName.Equals("TriggerSummary", StringComparison.OrdinalIgnoreCase)
            ? entry.After ?? entry.Before
            : null;
        var conditionSummary = valueName.Equals("ConditionSummary", StringComparison.OrdinalIgnoreCase)
            ? entry.After ?? entry.Before
            : null;
        var detail = action is not null
            ? $"{entry.Kind}: {taskName} command = {action}"
            : arguments is not null
                ? $"{entry.Kind}: {taskName} arguments = {arguments}"
                : workingDirectory is not null
                    ? $"{entry.Kind}: {taskName} working directory = {workingDirectory}"
                    : triggerSummary is not null
                        ? $"{entry.Kind}: {taskName} triggers = {triggerSummary}"
                        : conditionSummary is not null
                            ? $"{entry.Kind}: {taskName} conditions = {conditionSummary}"
                            : $"{entry.Kind}: {taskName} {valueName}".TrimEnd();

        item = new PersistenceItem(
            "scheduled-task",
            "high",
            entry.Kind.ToString(),
            taskName,
            detail,
            entry.Before,
            entry.After,
            Rank: 40);
        return true;
    }

    private static void AddFindingGroup(List<Finding> findings, IReadOnlyList<PersistenceItem> items, string kind, string severity, string title)
    {
        var matches = items.Where(item => item.Kind == kind).Take(5).ToList();
        if (matches.Count == 0)
        {
            return;
        }

        findings.Add(new Finding(severity, title, string.Join("; ", matches.Select(item => item.Detail))));
    }

    private static string? DescribeServiceStartMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim() switch
        {
            "0" => "Boot (0)",
            "1" => "System (1)",
            "2" => "Automatic (2)",
            "3" => "Manual (3)",
            "4" => "Disabled (4)",
            _ => value
        };
    }

    private static string? DescribeServiceType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim() switch
        {
            "1" => "Kernel driver (1)",
            "2" => "File system driver (2)",
            "16" => "Own process service (16)",
            "32" => "Shared process service (32)",
            "272" => "Interactive own process service (272)",
            "288" => "Interactive shared process service (288)",
            _ => value
        };
    }

    private static bool IsStartupRegistry(string key) =>
        key.Contains("\\Microsoft\\Windows\\CurrentVersion\\Run\\", StringComparison.OrdinalIgnoreCase)
        || key.Contains("\\Microsoft\\Windows\\CurrentVersion\\RunOnce\\", StringComparison.OrdinalIgnoreCase)
        || key.Contains("\\Explorer\\StartupApproved\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsProtocolHandler(string key) =>
        key.Contains("\\Software\\Classes\\", StringComparison.OrdinalIgnoreCase)
        && (key.Contains("\\URL Protocol", StringComparison.OrdinalIgnoreCase)
            || key.Contains("\\shell\\open\\command\\", StringComparison.OrdinalIgnoreCase));

    private static bool IsFileAssociation(string key) =>
        key.Contains("\\Explorer\\FileExts\\", StringComparison.OrdinalIgnoreCase)
        || key.Contains("\\Software\\Classes\\.", StringComparison.OrdinalIgnoreCase);

    private static bool IsStartupFolderPath(string path) =>
        path.Replace('/', '\\').Contains("\\Microsoft\\Windows\\Start Menu\\Programs\\Startup\\", StringComparison.OrdinalIgnoreCase);

    private static string ExtractProtocolName(string key) =>
        ExtractAfterMarker(key, "\\Software\\Classes\\");

    private static string ExtractFileAssociationName(string key)
    {
        var fromFileExts = ExtractAfterMarker(key, "\\Explorer\\FileExts\\");
        if (!string.Equals(fromFileExts, key, StringComparison.Ordinal))
        {
            return fromFileExts;
        }

        return ExtractAfterMarker(key, "\\Software\\Classes\\");
    }

    private static string ExtractAfterMarker(string key, string marker)
    {
        var index = key.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return LastSegment(key);
        }

        var relative = key[(index + marker.Length)..];
        var first = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? LastSegment(key) : first;
    }

    private static string LastSegment(string key)
    {
        var parts = key.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? key : parts[^1];
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}

internal sealed record PersistenceSummary(
    IReadOnlyList<PersistenceItem> Items,
    int StartupCount,
    int ServiceCount,
    int ScheduledTaskCount,
    int ProtocolHandlerCount,
    int FileAssociationCount)
{
    public bool HasChanges => Items.Count > 0;
}

internal sealed record PersistenceItem(
    string Kind,
    string Severity,
    string Action,
    string Name,
    string Detail,
    string? Before,
    string? After,
    int Rank);
