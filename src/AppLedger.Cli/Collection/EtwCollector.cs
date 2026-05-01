namespace AppLedger;

internal sealed class EtwCollector : IDisposable
{
    private readonly IReadOnlyList<string> _watchRoots;
    private readonly bool _watchAll;
    private readonly bool _captureReads;
    private readonly int? _maxEvents;
    private readonly PathFilter _pathFilter;
    private readonly ConcurrentDictionary<int, byte> _sessionPids = new();
    private readonly ConcurrentDictionary<int, ProcessRecord> _processes = new();
    private readonly ConcurrentQueue<FileEvent> _fileEvents = new();
    private readonly ConcurrentDictionary<ulong, string> _knownFilePaths = new();
    private readonly ConcurrentDictionary<ulong, PendingRename> _pendingRenames = new();
    private readonly TraceEventSession? _session;
    private Task? _processingTask;
    private ProcessSampler? _processSampler;
    private volatile bool _disposed;
    private int _eventCount;

    private EtwCollector(IReadOnlyList<string> watchRoots, bool watchAll, bool captureReads, int? maxEvents, PathFilter pathFilter, TraceEventSession? session, Task? processingTask, string? status)
    {
        _watchRoots = watchRoots;
        _watchAll = watchAll;
        _captureReads = captureReads;
        _maxEvents = maxEvents;
        _pathFilter = pathFilter;
        _session = session;
        _processingTask = processingTask;
        Status = status;
    }

    public bool IsRunning => _session is not null;

    public string? Status { get; }

    public IReadOnlyList<FileEvent> FileEvents => _fileEvents.ToArray();

    public IReadOnlyList<ProcessRecord> Processes => _processes.Values.ToArray();

    public bool HasReachedEventLimit => _maxEvents is not null && Volatile.Read(ref _eventCount) >= _maxEvents.Value;

    public static EtwCollector TryStart(IReadOnlyList<string> watchRoots, bool watchAll, bool captureReads, int? maxEvents)
        => TryStart(watchRoots, watchAll, captureReads, maxEvents, PathFilter.Empty);

    public static EtwCollector TryStart(IReadOnlyList<string> watchRoots, bool watchAll, bool captureReads, int? maxEvents, PathFilter pathFilter)
    {
        if (TraceEventSession.IsElevated() != true)
        {
            return new EtwCollector(watchRoots, watchAll, captureReads, maxEvents, pathFilter, null, null, "run from an elevated terminal for live ETW file events");
        }

        try
        {
            var session = new TraceEventSession(KernelTraceEventParser.KernelSessionName) { StopOnDispose = true };
            var collector = new EtwCollector(watchRoots, watchAll, captureReads, maxEvents, pathFilter, session, null, null);
            session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.Process
                | KernelTraceEventParser.Keywords.FileIO
                | KernelTraceEventParser.Keywords.FileIOInit);
            collector.Configure(session);
            var task = Task.Run(() =>
            {
                try
                {
                    session.Source.Process();
                }
                catch (ObjectDisposedException)
                {
                    // Normal during shutdown.
                }
            });

            collector._processingTask = task;
            return collector;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or COMException)
        {
            return new EtwCollector(watchRoots, watchAll, captureReads, maxEvents, pathFilter, null, null, ex.Message);
        }
    }

    public void AttachSession(int rootPid, ProcessSampler processSampler)
    {
        _processSampler = processSampler;
        _sessionPids[rootPid] = 0;
        SyncSessionProcesses(processSampler.SessionProcessIds);
    }

    public void SyncSessionProcesses(IReadOnlySet<int> processIds)
    {
        foreach (var pid in _sessionPids.Keys)
        {
            if (!processIds.Contains(pid))
            {
                _sessionPids.TryRemove(pid, out _);
            }
        }

        foreach (var pid in processIds)
        {
            _sessionPids[pid] = 0;
        }
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _session?.Stop();
            _processingTask?.Wait(TimeSpan.FromSeconds(3));
            FlushPendingRenames();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or AggregateException or InvalidOperationException)
        {
            // Best-effort shutdown.
        }
    }

    public void Dispose()
    {
        Stop();
        _session?.Dispose();
    }

    private void Configure(TraceEventSession session)
    {
        session.Source.Kernel.ProcessStart += data =>
        {
            var record = new ProcessRecord(
                data.ProcessID,
                data.ParentID,
                string.IsNullOrWhiteSpace(data.ProcessName) ? Path.GetFileName(data.ImageFileName) : data.ProcessName,
                data.ImageFileName,
                data.CommandLine,
                data.TimeStamp,
                data.TimeStamp,
                data.TimeStamp);

            if (_processSampler?.Observe(record) == true)
            {
                _sessionPids[data.ProcessID] = 0;
                _processes[data.ProcessID] = record;
            }
            else if (_processSampler is null && _sessionPids.ContainsKey(data.ParentID))
            {
                _sessionPids[data.ProcessID] = 0;
                _processes[data.ProcessID] = record;
            }
        };

        session.Source.Kernel.FileIOName += HandleFileName;
        session.Source.Kernel.FileIOFileCreate += HandleFileName;
        session.Source.Kernel.FileIOCreate += HandleFileCreate;
        if (_captureReads)
        {
            session.Source.Kernel.FileIORead += data => AddFile(FileEventKind.Read, data.FileName, data.ProcessID, data.ProcessName, data.TimeStamp);
        }
        session.Source.Kernel.FileIOWrite += data => AddFile(FileEventKind.Modified, data.FileName, data.ProcessID, data.ProcessName, data.TimeStamp);
        session.Source.Kernel.FileIOFileDelete += data => AddFile(FileEventKind.Deleted, data.FileName, data.ProcessID, data.ProcessName, data.TimeStamp);
        session.Source.Kernel.FileIODelete += data => AddFile(FileEventKind.Deleted, data.FileName, data.ProcessID, data.ProcessName, data.TimeStamp);
        session.Source.Kernel.FileIORename += HandleFileRename;
    }

    private void HandleFileName(FileIONameTraceData data)
    {
        if (string.IsNullOrWhiteSpace(data.FileName) || data.FileKey == 0)
        {
            return;
        }

        if (!ShouldTrackProcess(data.ProcessID))
        {
            return;
        }

        _knownFilePaths[data.FileKey] = data.FileName;

        if (_pendingRenames.TryGetValue(data.FileKey, out var pending)
            && !NormalizePath(pending.FromPath).Equals(NormalizePath(data.FileName), StringComparison.OrdinalIgnoreCase))
        {
            _pendingRenames.TryRemove(data.FileKey, out _);
            if (FileEventMerger.IsPlausibleRenamePair(pending.FromPath, data.FileName))
            {
                AddFile(FileEventKind.Renamed, pending.FromPath, pending.ProcessId, pending.ProcessName, data.TimeStamp, data.FileName);
            }

            _knownFilePaths[data.FileKey] = data.FileName;
        }
    }

    private void HandleFileRename(FileIOInfoTraceData data)
    {
        if (string.IsNullOrWhiteSpace(data.FileName))
        {
            return;
        }

        if (!ShouldTrackProcess(data.ProcessID))
        {
            return;
        }

        if (data.FileKey != 0
            && _knownFilePaths.TryGetValue(data.FileKey, out var currentPath)
            && !NormalizePath(currentPath).Equals(NormalizePath(data.FileName), StringComparison.OrdinalIgnoreCase))
        {
            if (FileEventMerger.IsPlausibleRenamePair(currentPath, data.FileName))
            {
                AddFile(FileEventKind.Renamed, currentPath, data.ProcessID, data.ProcessName, data.TimeStamp, data.FileName);
            }

            _knownFilePaths[data.FileKey] = data.FileName;
            return;
        }

        if (data.FileKey != 0)
        {
            _pendingRenames[data.FileKey] = new PendingRename(data.FileName, data.ProcessID, data.ProcessName, data.TimeStamp);
            _knownFilePaths[data.FileKey] = data.FileName;
            return;
        }

        AddFile(FileEventKind.Renamed, data.FileName, data.ProcessID, data.ProcessName, data.TimeStamp);
    }

    private void HandleFileCreate(FileIOCreateTraceData data)
    {
        var kind = data.CreateDisposition switch
        {
            CreateDisposition.CREATE_NEW => FileEventKind.Created,
            CreateDisposition.CREATE_ALWAYS => FileEventKind.Created,
            CreateDisposition.SUPERSEDE => FileEventKind.Created,
            CreateDisposition.TRUNCATE_EXISTING => FileEventKind.Modified,
            _ => (FileEventKind?)null
        };

        if (kind is not null)
        {
            AddFile(kind.Value, data.FileName, data.ProcessID, data.ProcessName, data.TimeStamp);
        }
    }

    private void AddFile(FileEventKind kind, string? path, int processId, string? processName, DateTime timestamp, string? relatedPath = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var pathInScope = _watchAll || PathClassifier.IsUnderAnyWatchRoot(path, _watchRoots);
        var relatedPathInScope = !string.IsNullOrWhiteSpace(relatedPath) && (_watchAll || PathClassifier.IsUnderAnyWatchRoot(relatedPath, _watchRoots));
        if (!pathInScope && !relatedPathInScope)
        {
            return;
        }

        if (!_pathFilter.AllowsAny(path, relatedPath))
        {
            return;
        }

        if (!ShouldTrackProcess(processId))
        {
            return;
        }

        if (IsNoisyFileEvent(kind, path))
        {
            return;
        }

        if (HasReachedEventLimit)
        {
            return;
        }

        _fileEvents.Enqueue(FileEvent.Live(kind, path, processId, processName, relatedPath) with { ObservedAt = timestamp });
        Interlocked.Increment(ref _eventCount);
    }

    private bool ShouldTrackProcess(int processId)
    {
        if (_sessionPids.ContainsKey(processId))
        {
            return true;
        }

        if (_processSampler?.ContainsProcess(processId) == true)
        {
            _sessionPids[processId] = 0;
            return true;
        }

        return false;
    }

    private void FlushPendingRenames()
    {
        foreach (var (_, pending) in _pendingRenames.ToArray())
        {
            if (!_pathFilter.Allows(pending.FromPath))
            {
                continue;
            }

            _fileEvents.Enqueue(FileEvent.Live(FileEventKind.Renamed, pending.FromPath, pending.ProcessId, pending.ProcessName) with
            {
                ObservedAt = pending.Timestamp
            });
        }

        _pendingRenames.Clear();
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

    private static bool IsNoisyFileEvent(FileEventKind kind, string path)
    {
        if (kind != FileEventKind.Modified)
        {
            return IsIgnoredGitLockPath(path);
        }

        var fileName = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(fileName) || IsIgnoredGitLockPath(path);
    }

    private static bool IsIgnoredGitLockPath(string path)
    {
        var normalized = path.Replace('/', '\\');
        return normalized.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase)
            && normalized.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record PendingRename(string FromPath, int ProcessId, string? ProcessName, DateTime Timestamp);
}
