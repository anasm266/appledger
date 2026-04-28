namespace AppLedger;

internal sealed class ProcessSampler
{
    private readonly int _rootPid;
    private readonly HashSet<int> _sessionPids = [];
    private readonly object _lock = new();

    public ProcessSampler(int rootPid, string target, string arguments)
    {
        _rootPid = rootPid;
        _sessionPids.Add(rootPid);
        Processes[rootPid] = new ProcessRecord(
            rootPid,
            0,
            Path.GetFileName(target),
            target,
            string.IsNullOrWhiteSpace(arguments) ? target : $"{target} {arguments}",
            DateTimeOffset.Now,
            DateTimeOffset.Now,
            DateTimeOffset.Now);
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

    public void Observe(ProcessRecord record)
    {
        lock (_lock)
        {
            if (record.ProcessId == _rootPid || _sessionPids.Contains(record.ParentProcessId))
            {
                _sessionPids.Add(record.ProcessId);
                Processes.AddOrUpdate(
                    record.ProcessId,
                    _ => record,
                    (_, existing) => MergeRecord(existing, record));
            }
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
        foreach (var observed in WmiProcess.ReadAll())
        {
            Observe(observed.ToRecord());
        }
    }

    public int CountActiveSessionProcesses()
    {
        var active = 0;
        foreach (var pid in _sessionPids.ToArray())
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

    public ProcessIdentity Identity => ProcessIdentity.From(this);
}
