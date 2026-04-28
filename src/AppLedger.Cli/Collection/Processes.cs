namespace AppLedger;

internal sealed class ProcessSampler
{
    private readonly int _rootPid;
    private readonly HashSet<int> _sessionPids = [];
    private readonly Dictionary<int, ProcessRecord> _activeSessionProcesses = [];
    private readonly object _lock = new();

    public ProcessSampler(int rootPid, string target, string arguments)
    {
        _rootPid = rootPid;
        var root = new ProcessRecord(
            rootPid,
            0,
            Path.GetFileName(target),
            target,
            string.IsNullOrWhiteSpace(arguments) ? target : $"{target} {arguments}",
            null,
            DateTimeOffset.Now,
            DateTimeOffset.Now);
        AddSessionRecord(root);
    }

    public ConcurrentDictionary<int, ProcessRecord> Processes { get; } = new();

    public IReadOnlySet<int> SessionProcessIds
    {
        get
        {
            lock (_lock)
            {
                return _sessionPids.ToHashSet();
            }
        }
    }

    public bool ContainsProcess(int pid)
    {
        lock (_lock)
        {
            return _sessionPids.Contains(pid);
        }
    }

    public bool Observe(ProcessRecord record)
    {
        lock (_lock)
        {
            if (record.ProcessId == _rootPid || CanAdoptChild(record, currentProcessesByPid: null))
            {
                return AddSessionRecord(record);
            }

            return false;
        }
    }

    public void Merge(IEnumerable<ProcessRecord> records)
    {
        foreach (var record in records)
        {
            Observe(record);
        }
    }

    public void Sample()
    {
        SampleFrom(WmiProcess.ReadAll().Select(process => process.ToRecord()));
    }

    internal void SampleFrom(IEnumerable<ProcessRecord> records)
    {
        var snapshot = records
            .GroupBy(record => record.ProcessId)
            .Select(group => group.OrderByDescending(record => record.CreationDate ?? DateTimeOffset.MinValue).First())
            .ToDictionary(record => record.ProcessId);

        lock (_lock)
        {
            PruneReusedSessionPids(snapshot);

            if (snapshot.TryGetValue(_rootPid, out var root))
            {
                AddSessionRecord(root);
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var record in snapshot.Values)
                {
                    if (_sessionPids.Contains(record.ProcessId))
                    {
                        if (IsSameProcessInstance(_activeSessionProcesses[record.ProcessId], record))
                        {
                            AddSessionRecord(record);
                        }

                        continue;
                    }

                    if (CanAdoptChild(record, snapshot))
                    {
                        changed |= AddSessionRecord(record);
                    }
                }
            }
        }
    }

    public int CountActiveSessionProcesses()
    {
        var active = 0;
        int[] sessionPids;
        lock (_lock)
        {
            sessionPids = _sessionPids.ToArray();
        }

        foreach (var pid in sessionPids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited)
                {
                    active++;
                }
            }
            catch (ArgumentException)
            {
                // Process exited between samples.
            }
            catch (InvalidOperationException)
            {
                // Process exited between samples.
            }
        }

        return active;
    }

    private static ProcessRecord MergeRecord(ProcessRecord existing, ProcessRecord incoming) =>
        existing with
        {
            ParentProcessId = existing.ParentProcessId == 0 ? incoming.ParentProcessId : existing.ParentProcessId,
            Name = string.IsNullOrWhiteSpace(existing.Name) ? incoming.Name : existing.Name,
            ExecutablePath = existing.ExecutablePath ?? incoming.ExecutablePath,
            CommandLine = existing.CommandLine ?? incoming.CommandLine,
            CreationDate = existing.CreationDate ?? incoming.CreationDate,
            FirstSeen = existing.FirstSeen <= incoming.FirstSeen ? existing.FirstSeen : incoming.FirstSeen,
            LastSeen = existing.LastSeen >= incoming.LastSeen ? existing.LastSeen : incoming.LastSeen
        };

    private bool AddSessionRecord(ProcessRecord record)
    {
        if (_activeSessionProcesses.TryGetValue(record.ProcessId, out var active)
            && !IsSameProcessInstance(active, record))
        {
            return false;
        }

        _sessionPids.Add(record.ProcessId);
        _activeSessionProcesses[record.ProcessId] = _activeSessionProcesses.TryGetValue(record.ProcessId, out active)
            ? MergeRecord(active, record)
            : record;

        Processes.AddOrUpdate(
            record.ProcessId,
            _ => record,
            (_, existing) => IsSameProcessInstance(existing, record) ? MergeRecord(existing, record) : existing);

        return true;
    }

    private bool CanAdoptChild(ProcessRecord record, IReadOnlyDictionary<int, ProcessRecord>? currentProcessesByPid)
    {
        if (!_sessionPids.Contains(record.ParentProcessId))
        {
            return false;
        }

        if (!_activeSessionProcesses.TryGetValue(record.ParentProcessId, out var knownParent))
        {
            return false;
        }

        if (currentProcessesByPid is not null
            && currentProcessesByPid.TryGetValue(record.ParentProcessId, out var currentParent)
            && !IsSameProcessInstance(knownParent, currentParent))
        {
            return false;
        }

        if (knownParent.CreationDate is not null
            && record.CreationDate is not null
            && record.CreationDate.Value < knownParent.CreationDate.Value.AddSeconds(-2))
        {
            return false;
        }

        return !_activeSessionProcesses.TryGetValue(record.ProcessId, out var active)
            || IsSameProcessInstance(active, record);
    }

    private void PruneReusedSessionPids(IReadOnlyDictionary<int, ProcessRecord> currentProcessesByPid)
    {
        foreach (var pid in _sessionPids.ToArray())
        {
            if (pid == _rootPid)
            {
                continue;
            }

            if (!_activeSessionProcesses.TryGetValue(pid, out var active)
                || !currentProcessesByPid.TryGetValue(pid, out var current))
            {
                continue;
            }

            if (!IsSameProcessInstance(active, current))
            {
                _sessionPids.Remove(pid);
                _activeSessionProcesses.Remove(pid);
            }
        }
    }

    private static bool IsSameProcessInstance(ProcessRecord existing, ProcessRecord incoming)
    {
        if (existing.CreationDate is null || incoming.CreationDate is null)
        {
            return true;
        }

        return existing.ProcessId == incoming.ProcessId
            && existing.CreationDate.Value.Equals(incoming.CreationDate.Value);
    }
}

internal sealed record WmiProcess(
    int ProcessId,
    int ParentProcessId,
    string Name,
    string? ExecutablePath,
    string? CommandLine,
    DateTimeOffset? CreationDate)
{
    public ProcessRecord ToRecord() => new(
        ProcessId,
        ParentProcessId,
        Name,
        ExecutablePath,
        CommandLine,
        CreationDate,
        DateTimeOffset.Now,
        DateTimeOffset.Now);

    public static IEnumerable<WmiProcess> ReadAll()
    {
        using var searcher = new ManagementObjectSearcher("SELECT ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine,CreationDate FROM Win32_Process");
        foreach (ManagementObject process in searcher.Get().Cast<ManagementObject>())
        {
            yield return new WmiProcess(
                Convert.ToInt32(process["ProcessId"], CultureInfo.InvariantCulture),
                Convert.ToInt32(process["ParentProcessId"], CultureInfo.InvariantCulture),
                Convert.ToString(process["Name"], CultureInfo.InvariantCulture) ?? "",
                Convert.ToString(process["ExecutablePath"], CultureInfo.InvariantCulture),
                Convert.ToString(process["CommandLine"], CultureInfo.InvariantCulture),
                ParseWmiDate(Convert.ToString(process["CreationDate"], CultureInfo.InvariantCulture)));
        }
    }

    private static DateTimeOffset? ParseWmiDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return ManagementDateTimeConverter.ToDateTime(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

internal sealed record ProcessRecord(
    int ProcessId,
    int ParentProcessId,
    string Name,
    string? ExecutablePath,
    string? CommandLine,
    DateTimeOffset? CreationDate,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen)
{
    public string? CommandLineHash => ProcessIdentity.HashCommandLine(CommandLine);

    public string ProcessInstanceKey => Identity.ProcessInstanceKey;

    public ProcessIdentity Identity => ProcessIdentity.From(this);
}
