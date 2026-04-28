namespace AppLedger;

internal sealed record SessionReport(
    string App,
    string Arguments,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    IReadOnlyList<string> WatchRoots,
    bool WatchAll,
    SessionSummary Summary,
    IReadOnlyList<FileEvent> FileEvents,
    IReadOnlyList<ProcessRecord> Processes,
    IReadOnlyList<NetworkEvent> NetworkEvents,
    IReadOnlyList<RegistryEvent> RegistryEvents,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<FolderImpact> TopFolders,
    AiCodingActivity? AiActivity,
    IReadOnlyList<string> SnapshotErrors,
    SessionActivityOverview? ActivityOverview = null,
    SessionNetworkOverview? NetworkOverview = null,
    SessionCaptureSettings? CaptureSettings = null)
{
    public static SessionReport Build(
        string app,
        string arguments,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        IReadOnlyList<string> watchRoots,
        bool watchAll,
        FileSnapshot before,
        FileSnapshot after,
        IReadOnlyList<FileEvent> fileEvents,
        IReadOnlyList<ProcessRecord> processes,
        IReadOnlyList<NetworkEvent> networkEvents,
        IReadOnlyList<RegistryEvent> registryEvents,
        SessionCaptureSettings? captureSettings = null)
    {
        var normalizedFileEvents = FileEventMerger.NormalizeForSession(fileEvents);
        var normalizedNetworkEvents = NetworkResolver.Enrich(networkEvents);
        var topFolders = BuildFolderImpact(normalizedFileEvents);
        var findings = Analyzer.Find(normalizedFileEvents, processes, normalizedNetworkEvents, registryEvents, topFolders);
        var aiActivity = AiCodingAnalyzer.Build(watchRoots, normalizedFileEvents, processes);
        var activityOverview = SessionActivityAnalyzer.Build(watchRoots, watchAll, normalizedFileEvents, normalizedNetworkEvents, aiActivity, findings);
        var networkOverview = NetworkSummaryAnalyzer.Build(normalizedNetworkEvents, processes);
        var summary = new SessionSummary(
            normalizedFileEvents.Count(e => e.Kind == FileEventKind.Read),
            normalizedFileEvents.Count(e => e.Kind == FileEventKind.Created),
            normalizedFileEvents.Count(e => e.Kind == FileEventKind.Modified),
            normalizedFileEvents.Count(e => e.Kind == FileEventKind.Deleted),
            normalizedFileEvents.Count(e => e.Kind == FileEventKind.Renamed),
            normalizedFileEvents.Where(e => e.Kind != FileEventKind.Deleted).Sum(e => Math.Max(0, e.SizeDelta)),
            processes.Count,
            processes.Count(p => !string.IsNullOrWhiteSpace(p.CommandLine)),
            normalizedNetworkEvents.Count,
            registryEvents.Count,
            findings.Count(f => f.Severity == "high" || f.Severity == "medium"));

        return new SessionReport(
            app,
            arguments,
            startedAt,
            endedAt,
            watchRoots,
            watchAll,
            summary,
            normalizedFileEvents,
            processes,
            normalizedNetworkEvents,
            registryEvents,
            findings,
            topFolders,
            aiActivity,
            before.Errors.Concat(after.Errors).Distinct().Take(200).ToList(),
            activityOverview,
            networkOverview,
            captureSettings ?? SessionCaptureSettings.Default(watchAll));
    }

    public static SessionReport RefreshDerivedData(SessionReport session)
    {
        var normalizedFileEvents = FileEventMerger.NormalizeForSession(session.FileEvents);
        var normalizedNetworkEvents = NetworkResolver.Enrich(session.NetworkEvents);
        var topFolders = BuildFolderImpact(normalizedFileEvents);
        var findings = Analyzer.Find(normalizedFileEvents, session.Processes, normalizedNetworkEvents, session.RegistryEvents, topFolders);
        var aiActivity = AiCodingAnalyzer.Build(session.WatchRoots, normalizedFileEvents, session.Processes);
        var activityOverview = SessionActivityAnalyzer.Build(session.WatchRoots, session.WatchAll, normalizedFileEvents, normalizedNetworkEvents, aiActivity, findings);
        var networkOverview = NetworkSummaryAnalyzer.Build(normalizedNetworkEvents, session.Processes);
        var summary = new SessionSummary(
            normalizedFileEvents.Count(e => e.Kind == FileEventKind.Read),
            normalizedFileEvents.Count(e => e.Kind == FileEventKind.Created),
            normalizedFileEvents.Count(e => e.Kind == FileEventKind.Modified),
            normalizedFileEvents.Count(e => e.Kind == FileEventKind.Deleted),
            normalizedFileEvents.Count(e => e.Kind == FileEventKind.Renamed),
            normalizedFileEvents.Where(e => e.Kind != FileEventKind.Deleted).Sum(e => Math.Max(0, e.SizeDelta)),
            session.Processes.Count,
            session.Processes.Count(p => !string.IsNullOrWhiteSpace(p.CommandLine)),
            normalizedNetworkEvents.Count,
            session.RegistryEvents.Count,
            findings.Count(f => f.Severity is "high" or "medium"));

        return session with
        {
            Summary = summary,
            FileEvents = normalizedFileEvents,
            NetworkEvents = normalizedNetworkEvents,
            Findings = findings,
            TopFolders = topFolders,
            AiActivity = aiActivity,
            ActivityOverview = activityOverview,
            NetworkOverview = networkOverview,
            CaptureSettings = session.CaptureSettings ?? SessionCaptureSettings.Default(session.WatchAll)
        };
    }

    private static List<FolderImpact> BuildFolderImpact(IReadOnlyList<FileEvent> events)
    {
        return events
            .Where(e => e.Kind is FileEventKind.Created or FileEventKind.Modified or FileEventKind.Renamed)
            .GroupBy(e => Path.GetDirectoryName(e.Kind == FileEventKind.Renamed && !string.IsNullOrWhiteSpace(e.RelatedPath) ? e.RelatedPath : e.Path) ?? e.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => new FolderImpact(
                group.Key,
                group.Count(),
                group.Sum(e => Math.Max(0, e.SizeDelta)),
                PathClassifier.Classify(group.Key)))
            .OrderByDescending(f => f.BytesAdded)
            .ThenByDescending(f => f.FileCount)
            .Take(25)
            .ToList();
    }
}

internal sealed record SessionSummary(
    int FilesRead,
    int FilesCreated,
    int FilesModified,
    int FilesDeleted,
    int FilesRenamed,
    long BytesAddedOrChanged,
    int ProcessCount,
    int CommandCount,
    int NetworkConnectionCount,
    int RegistryChangeCount,
    int RiskObservationCount);

internal sealed record SessionCaptureSettings(
    string? Profile,
    bool WatchAll,
    bool CaptureReads,
    int? MaxEvents,
    bool WriteSqlite)
{
    public static SessionCaptureSettings Default(bool watchAll) =>
        new(null, watchAll, CaptureReads: true, MaxEvents: null, WriteSqlite: true);
}

internal sealed record SessionActivityOverview(
    string Headline,
    IReadOnlyList<string> Highlights,
    IReadOnlyList<ActivityBucketSummary> Buckets);

internal sealed record SessionNetworkOverview(
    IReadOnlyList<NetworkDestinationSummary> Destinations,
    IReadOnlyList<NetworkProcessSummary> Processes);

internal sealed record NetworkDestinationSummary(
    string HostLabel,
    string DisplayAddress,
    int ConnectionCount,
    int ProcessCount,
    IReadOnlyList<string> Processes,
    IReadOnlyList<int> Ports,
    IReadOnlyList<string> Addresses);

internal sealed record NetworkProcessSummary(
    string ProcessName,
    int ConnectionCount,
    int DestinationCount,
    IReadOnlyList<string> Destinations);

internal sealed record ActivityBucketSummary(
    string Key,
    string Label,
    string Description,
    int EventCount,
    int UniquePathCount,
    long BytesChanged,
    IReadOnlyList<string> Examples);

internal sealed record FolderImpact(string Path, int FileCount, long BytesAdded, string Category);

internal sealed record Finding(string Severity, string Title, string Detail);

internal sealed record AiCodingActivity(
    ProjectChangeSummary ProjectChanges,
    IReadOnlyList<ProjectFileChange> ChangedProjectFiles,
    CommandSummary Commands,
    IReadOnlyList<CommandActivity> DeveloperCommands,
    IReadOnlyList<SensitiveAccess> SensitiveAccesses,
    IReadOnlyList<ProcessGroupSummary> ProcessGroups,
    IReadOnlyList<ProcessTimelineItem> ProcessTimeline);

internal sealed record ProjectChangeSummary(int Created, int Modified, int Deleted, int Renamed, int TotalChanged);

internal sealed record ProjectFileChange(FileEventKind Kind, string Path, string RelativePath, string? RelatedPath, string? RelatedRelativePath, string Category, string Source, DateTimeOffset ObservedAt);

internal sealed record CommandSummary(int Total, int PackageInstalls, int GitCommands, int TestCommands, int ShellCommands, int ScriptCommands);

internal sealed record CommandActivity(string Kind, int ProcessId, int ParentProcessId, string ProcessName, string CommandLine, DateTimeOffset FirstSeen, int Occurrences);

internal sealed record SensitiveAccess(FileEventKind Kind, string Path, string RelativePath, string Source, int? ProcessId, string? ProcessName, DateTimeOffset ObservedAt);

internal sealed record ProcessGroupSummary(string Name, int Count, int WithCommandLine, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);

internal sealed record ProcessTimelineItem(int ProcessId, int ParentProcessId, string Name, string? CommandLine, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, double DurationSeconds);
