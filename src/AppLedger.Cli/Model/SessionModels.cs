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
    SessionCaptureSettings? CaptureSettings = null,
    PersistenceSummary? Persistence = null)
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
        var processLookup = BuildProcessIdentityLookup(processes);
        var normalizedFileEvents = AttachProcessIdentities(FileEventMerger.NormalizeForSession(fileEvents), processLookup);
        var normalizedNetworkEvents = AttachProcessIdentities(NetworkResolver.Enrich(networkEvents), processLookup);
        var topFolders = BuildFolderImpact(normalizedFileEvents);
        var findings = Analyzer.Find(watchRoots, watchAll, normalizedFileEvents, processes, normalizedNetworkEvents, registryEvents, topFolders);
        var aiActivity = AiCodingAnalyzer.Build(watchRoots, normalizedFileEvents, processes);
        var activityOverview = SessionActivityAnalyzer.Build(watchRoots, watchAll, normalizedFileEvents, normalizedNetworkEvents, aiActivity, findings);
        var networkOverview = NetworkSummaryAnalyzer.Build(normalizedNetworkEvents, processes);
        var persistence = PersistenceAnalyzer.Build(normalizedFileEvents, registryEvents);
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
            captureSettings ?? SessionCaptureSettings.Default(watchAll),
            persistence);
    }

    public static SessionReport RefreshDerivedData(SessionReport session)
    {
        var processLookup = BuildProcessIdentityLookup(session.Processes);
        var normalizedFileEvents = AttachProcessIdentities(FileEventMerger.NormalizeForSession(session.FileEvents), processLookup);
        var normalizedNetworkEvents = AttachProcessIdentities(NetworkResolver.Enrich(session.NetworkEvents), processLookup);
        var topFolders = BuildFolderImpact(normalizedFileEvents);
        var findings = Analyzer.Find(session.WatchRoots, session.WatchAll, normalizedFileEvents, session.Processes, normalizedNetworkEvents, session.RegistryEvents, topFolders);
        var aiActivity = AiCodingAnalyzer.Build(session.WatchRoots, normalizedFileEvents, session.Processes);
        var activityOverview = SessionActivityAnalyzer.Build(session.WatchRoots, session.WatchAll, normalizedFileEvents, normalizedNetworkEvents, aiActivity, findings);
        var networkOverview = NetworkSummaryAnalyzer.Build(normalizedNetworkEvents, session.Processes);
        var persistence = PersistenceAnalyzer.Build(normalizedFileEvents, session.RegistryEvents);
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
            CaptureSettings = session.CaptureSettings ?? SessionCaptureSettings.Default(session.WatchAll),
            Persistence = persistence
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

    private static Dictionary<int, List<ProcessRecord>> BuildProcessIdentityLookup(IReadOnlyList<ProcessRecord> processes) =>
        processes
            .GroupBy(process => process.ProcessId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(process => process.CreationDate ?? process.FirstSeen)
                    .ThenBy(process => process.FirstSeen)
                    .ToList());

    private static List<FileEvent> AttachProcessIdentities(IReadOnlyList<FileEvent> events, IReadOnlyDictionary<int, List<ProcessRecord>> processes) =>
        events
            .Select(file =>
            {
                if (file.ProcessId is null)
                {
                    return file with { Attribution = file.Attribution ?? Attribution.Low("No process ID was available for this event.") };
                }

                var resolved = ResolveProcessIdentity(file.ProcessId.Value, file.ObservedAt, processes);
                return file with
                {
                    Process = file.Process ?? resolved.Identity,
                    Attribution = file.Attribution ?? resolved.Attribution
                };
            })
            .ToList();

    private static List<NetworkEvent> AttachProcessIdentities(IReadOnlyList<NetworkEvent> events, IReadOnlyDictionary<int, List<ProcessRecord>> processes) =>
        events
            .Select(item =>
            {
                var resolved = ResolveProcessIdentity(item.ProcessId, item.FirstSeen, processes);
                return item with
                {
                    Process = item.Process ?? resolved.Identity,
                    Attribution = item.Attribution ?? resolved.Attribution
                };
            })
            .ToList();

    internal static ProcessAttribution ResolveProcessIdentity(int processId, DateTimeOffset observedAt, IReadOnlyDictionary<int, List<ProcessRecord>> processes)
    {
        if (!processes.TryGetValue(processId, out var candidates) || candidates.Count == 0)
        {
            return new ProcessAttribution(
                null,
                Attribution.Low("PID was not found in the observed process table; attribution is PID-only and unverified."));
        }

        var nearObserved = candidates
            .Where(process =>
                process.FirstSeen <= observedAt.AddSeconds(2)
                && process.LastSeen >= observedAt.AddSeconds(-2))
            .OrderByDescending(process => process.CreationDate ?? process.FirstSeen)
            .FirstOrDefault();

        if (nearObserved is not null)
        {
            var confidence = nearObserved.CreationDate is null
                ? Attribution.Medium("PID matched a known session process, but process creation time was unavailable.")
                : Attribution.High("PID matched a known process instance with creation time and observed event window.");
            return new ProcessAttribution(nearObserved.Identity, confidence);
        }

        var nearest = candidates
            .OrderBy(process => Math.Abs((process.FirstSeen - observedAt).TotalMilliseconds))
            .ThenByDescending(process => process.LastSeen)
            .First();
        var fallbackConfidence = nearest.CreationDate is null
            ? Attribution.Low("PID matched a process table entry outside the observed event window, and creation time was unavailable.")
            : Attribution.Low("PID matched a process table entry outside the observed event window; possible late sampling or PID reuse.");
        return new ProcessAttribution(nearest.Identity, fallbackConfidence);
    }
}

internal sealed record ProcessAttribution(ProcessIdentity? Identity, Attribution Attribution);

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
    bool WriteSqlite,
    IReadOnlyList<string>? IncludeFilters = null,
    IReadOnlyList<string>? ExcludeFilters = null)
{
    public static SessionCaptureSettings Default(bool watchAll) =>
        new(null, watchAll, CaptureReads: true, MaxEvents: null, WriteSqlite: true, IncludeFilters: [], ExcludeFilters: []);
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
