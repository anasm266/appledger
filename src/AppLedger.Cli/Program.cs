using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace AppLedger;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "run" => await RunAsync(args.Skip(1).ToArray()),
                "apps" => Apps(args.Skip(1).ToArray()),
                "snapshot" => Snapshot(args.Skip(1).ToArray()),
                "diff" => Diff(args.Skip(1).ToArray()),
                _ => Fail($"Unknown command: {args[0]}")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AppLedger failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var options = RunOptions.Parse(args);
        if (options is null)
        {
            return 1;
        }

        Directory.CreateDirectory(options.OutputDirectory);

        Console.WriteLine("AppLedger session recorder");
        Console.WriteLine($"  App:       {options.Target}");
        Console.WriteLine($"  Output:    {options.OutputDirectory}");
        Console.WriteLine($"  Watch:     {string.Join("; ", options.WatchRoots)}");
        Console.WriteLine();

        Console.WriteLine("Taking before snapshot...");
        var before = FileSnapshot.Capture(options.WatchRoots);
        var registryBefore = RegistrySnapshot.Capture();

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
            Console.WriteLine("Stopping recording...");
        };

        if (options.Timeout is not null)
        {
            stop.CancelAfter(options.Timeout.Value);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = options.Target,
            Arguments = options.AppArguments,
            UseShellExecute = false,
            WorkingDirectory = options.WorkingDirectory
        };

        var process = Process.Start(startInfo);
        if (process is null)
        {
            return Fail("Could not start target process.");
        }

        var startedAt = DateTimeOffset.Now;
        var processSampler = new ProcessSampler(process.Id, options.Target, options.AppArguments);
        var networkSampler = new NetworkSampler();

        Console.WriteLine($"Recording PID {process.Id}. Press Ctrl+C to stop.");
        var emptyTreeSamples = 0;

        while (!stop.IsCancellationRequested)
        {
            processSampler.Sample();
            networkSampler.Sample(processSampler.SessionProcessIds);

            var activeCount = processSampler.CountActiveSessionProcesses();
            if (activeCount == 0)
            {
                emptyTreeSamples++;
                if (emptyTreeSamples >= 3)
                {
                    break;
                }
            }
            else
            {
                emptyTreeSamples = 0;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(750), stop.Token).ContinueWith(_ => { });
        }

        var endedAt = DateTimeOffset.Now;
        Console.WriteLine("Taking after snapshot...");
        var after = FileSnapshot.Capture(options.WatchRoots);
        var registryAfter = RegistrySnapshot.Capture();

        var fileEvents = FileDiff.Compare(before, after);
        var registryEvents = RegistrySnapshot.Compare(registryBefore, registryAfter);
        var session = SessionReport.Build(
            options.Target,
            options.AppArguments,
            startedAt,
            endedAt,
            options.WatchRoots,
            before,
            after,
            fileEvents,
            processSampler.Processes.Values.OrderBy(p => p.FirstSeen).ToList(),
            networkSampler.Events.OrderBy(e => e.FirstSeen).ToList(),
            registryEvents);

        var jsonPath = Path.Combine(options.OutputDirectory, "session.json");
        var htmlPath = Path.Combine(options.OutputDirectory, "report.html");
        var csvPath = Path.Combine(options.OutputDirectory, "touched-files.csv");
        var commandsPath = Path.Combine(options.OutputDirectory, "commands.json");
        var cleanupPath = Path.Combine(options.OutputDirectory, "cleanup.ps1");

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(session, JsonOptions), Encoding.UTF8);
        await File.WriteAllTextAsync(htmlPath, HtmlReport.Render(session), Encoding.UTF8);
        await File.WriteAllTextAsync(csvPath, CsvReport.RenderFiles(session.FileEvents), Encoding.UTF8);
        await File.WriteAllTextAsync(commandsPath, JsonSerializer.Serialize(session.Processes.Where(p => !string.IsNullOrWhiteSpace(p.CommandLine)), JsonOptions), Encoding.UTF8);
        await File.WriteAllTextAsync(cleanupPath, CleanupScript.Render(session), Encoding.UTF8);

        Console.WriteLine();
        Console.WriteLine($"AppLedger Report: {Path.GetFileNameWithoutExtension(options.Target)}");
        Console.WriteLine($"  Files created:  {session.Summary.FilesCreated}");
        Console.WriteLine($"  Files modified: {session.Summary.FilesModified}");
        Console.WriteLine($"  Files deleted:  {session.Summary.FilesDeleted}");
        Console.WriteLine($"  Processes:      {session.Processes.Count}");
        Console.WriteLine($"  Commands:       {session.Processes.Count(p => !string.IsNullOrWhiteSpace(p.CommandLine))}");
        Console.WriteLine($"  Connections:    {session.NetworkEvents.Count}");
        Console.WriteLine($"  Findings:       {session.Findings.Count}");
        Console.WriteLine();
        Console.WriteLine("Generated:");
        Console.WriteLine($"  {htmlPath}");
        Console.WriteLine($"  {jsonPath}");
        Console.WriteLine($"  {csvPath}");
        Console.WriteLine($"  {commandsPath}");
        Console.WriteLine($"  {cleanupPath}");

        return 0;
    }

    private static int Apps(string[] args)
    {
        var query = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
        var apps = AppCatalog.Find(query)
            .Take(80)
            .ToList();

        if (apps.Count == 0)
        {
            Console.WriteLine(string.IsNullOrWhiteSpace(query)
                ? "No apps found."
                : $"No apps found for '{query}'.");
            return 0;
        }

        foreach (var app in apps)
        {
            Console.WriteLine($"{app.Name,-34} {app.Path}");
        }

        return 0;
    }

    private static int Snapshot(string[] args)
    {
        if (args.Length == 0)
        {
            return Fail("Usage: appledger snapshot <output.json> --watch <path> [--watch <path>]");
        }

        var output = args[0];
        var roots = Cli.GetRepeatedOption(args.Skip(1).ToArray(), "--watch").Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (roots.Count == 0)
        {
            roots.Add(Directory.GetCurrentDirectory());
        }

        var snapshot = FileSnapshot.Capture(roots);
        File.WriteAllText(output, JsonSerializer.Serialize(snapshot, JsonOptions), Encoding.UTF8);
        Console.WriteLine($"Captured {snapshot.Files.Count:N0} files into {output}");
        return 0;
    }

    private static int Diff(string[] args)
    {
        if (args.Length < 2)
        {
            return Fail("Usage: appledger diff <before.json> <after.json>");
        }

        var before = JsonSerializer.Deserialize<FileSnapshot>(File.ReadAllText(args[0]), JsonOptions)
            ?? throw new InvalidOperationException("Could not read before snapshot.");
        var after = JsonSerializer.Deserialize<FileSnapshot>(File.ReadAllText(args[1]), JsonOptions)
            ?? throw new InvalidOperationException("Could not read after snapshot.");
        var events = FileDiff.Compare(before, after);

        Console.WriteLine($"Created:  {events.Count(e => e.Kind == FileEventKind.Created),8:N0}");
        Console.WriteLine($"Modified: {events.Count(e => e.Kind == FileEventKind.Modified),8:N0}");
        Console.WriteLine($"Deleted:  {events.Count(e => e.Kind == FileEventKind.Deleted),8:N0}");
        foreach (var item in events.Take(50))
        {
            Console.WriteLine($"{item.Kind,-9} {item.Path}");
        }

        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        AppLedger - a black box recorder for Windows apps.

        Usage:
          appledger apps [search]
          appledger run <app name|alias|exe> [--args "<arguments>"] [--watch <path>] [--out <dir>] [--timeout <seconds>]
          appledger snapshot <output.json> --watch <path>
          appledger diff <before.json> <after.json>

        Examples:
          appledger apps code
          appledger run code --watch "C:\Users\Anas\Projects\demo-app"
          appledger run "C:\Windows\System32\notepad.exe" --watch "%USERPROFILE%\Documents"
          appledger run "C:\Path\To\Code.exe" --watch "C:\Users\Anas\Projects\demo-app"

        Notes:
          v0 records process trees and command lines live, samples IPv4 TCP connections,
          and uses before/after snapshots for created, modified, and deleted files.
        """);
    }
}

internal sealed record RunOptions(
    string Target,
    string AppArguments,
    string WorkingDirectory,
    string OutputDirectory,
    IReadOnlyList<string> WatchRoots,
    TimeSpan? Timeout)
{
    public static RunOptions? Parse(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: appledger run <app name|alias|exe> [--args \"...\"] [--watch <path>] [--out <dir>]");
            return null;
        }

        var targetQuery = Environment.ExpandEnvironmentVariables(args[0].Trim('"'));
        var target = AppCatalog.Resolve(targetQuery);
        if (target is null)
        {
            Console.Error.WriteLine($"Could not resolve app: {targetQuery}");
            Console.Error.WriteLine($"Try: appledger apps \"{targetQuery}\"");
            return null;
        }

        var appArgs = Cli.GetOption(args, "--args") ?? "";
        var output = Cli.GetOption(args, "--out")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "appledger-runs", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        output = Path.GetFullPath(Environment.ExpandEnvironmentVariables(output));

        var watchRoots = Cli.GetRepeatedOption(args, "--watch")
            .Select(Environment.ExpandEnvironmentVariables)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (watchRoots.Count == 0)
        {
            watchRoots.Add(Directory.GetCurrentDirectory());
            var temp = Path.GetTempPath();
            if (Directory.Exists(temp))
            {
                watchRoots.Add(temp);
            }
        }

        TimeSpan? timeout = null;
        if (int.TryParse(Cli.GetOption(args, "--timeout"), out var timeoutSeconds) && timeoutSeconds > 0)
        {
            timeout = TimeSpan.FromSeconds(timeoutSeconds);
        }

        return new RunOptions(
            Path.GetFullPath(target),
            appArgs,
            Directory.GetCurrentDirectory(),
            output,
            watchRoots,
            timeout);
    }
}

internal static class Cli
{
    public static string? GetOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    public static IEnumerable<string> GetRepeatedOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                yield return args[i + 1];
                i++;
            }
        }
    }
}

internal sealed record FileSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<string> Roots,
    Dictionary<string, FileState> Files,
    IReadOnlyList<string> Errors)
{
    public static FileSnapshot Capture(IReadOnlyList<string> roots)
    {
        var files = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        var errors = new ConcurrentBag<string>();

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in SafeEnumerateFiles(root, errors))
            {
                try
                {
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

    private static IEnumerable<string> SafeEnumerateFiles(string root, ConcurrentBag<string> errors)
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
                if (!PathClassifier.IsUsuallyNoise(directory))
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

internal sealed record FileEvent(
    FileEventKind Kind,
    string Path,
    long? SizeBefore,
    long? SizeAfter,
    long SizeDelta,
    DateTime? LastWriteBeforeUtc,
    DateTime? LastWriteAfterUtc,
    string Category,
    bool IsSensitive)
{
    public static FileEvent Created(string path, FileState current) =>
        New(FileEventKind.Created, path, null, current.Size, current.Size, null, current.LastWriteUtc);

    public static FileEvent Modified(string path, FileState previous, FileState current) =>
        New(FileEventKind.Modified, path, previous.Size, current.Size, current.Size - previous.Size, previous.LastWriteUtc, current.LastWriteUtc);

    public static FileEvent Deleted(string path, FileState previous) =>
        New(FileEventKind.Deleted, path, previous.Size, null, -previous.Size, previous.LastWriteUtc, null);

    private static FileEvent New(FileEventKind kind, string path, long? before, long? after, long delta, DateTime? beforeTime, DateTime? afterTime) =>
        new(kind, path, before, after, delta, beforeTime, afterTime, PathClassifier.Classify(path), PathClassifier.IsSensitive(path));
}

internal enum FileEventKind
{
    Created,
    Modified,
    Deleted
}

internal sealed class ProcessSampler
{
    private readonly int _rootPid;
    private readonly HashSet<int> _sessionPids = [];

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

    public IReadOnlySet<int> SessionProcessIds => _sessionPids;

    public void Sample()
    {
        foreach (var observed in WmiProcess.ReadAll())
        {
            if (observed.ProcessId == _rootPid || _sessionPids.Contains(observed.ParentProcessId))
            {
                _sessionPids.Add(observed.ProcessId);
                Processes.AddOrUpdate(
                    observed.ProcessId,
                    _ => observed.ToRecord(),
                    (_, existing) => existing with { LastSeen = DateTimeOffset.Now });
            }
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
    DateTimeOffset LastSeen);

internal sealed class NetworkSampler
{
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<NetworkEvent> _events = [];

    public IReadOnlyList<NetworkEvent> Events => _events;

    public void Sample(IReadOnlySet<int> sessionPids)
    {
        foreach (var row in TcpTable.ReadIPv4())
        {
            if (!sessionPids.Contains(row.ProcessId) || row.RemoteAddress == "0.0.0.0" || row.RemotePort == 0)
            {
                continue;
            }

            var key = $"{row.ProcessId}|{row.RemoteAddress}|{row.RemotePort}";
            if (_seen.Add(key))
            {
                _events.Add(new NetworkEvent(
                    row.ProcessId,
                    row.LocalAddress,
                    row.LocalPort,
                    row.RemoteAddress,
                    row.RemotePort,
                    row.State,
                    DateTimeOffset.Now));
            }
        }
    }
}

internal sealed record NetworkEvent(
    int ProcessId,
    string LocalAddress,
    int LocalPort,
    string RemoteAddress,
    int RemotePort,
    string State,
    DateTimeOffset FirstSeen);

internal static class TcpTable
{
    private const int AfInet = 2;

    public static IEnumerable<TcpRow> ReadIPv4()
    {
        var bufferLength = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref bufferLength, true, AfInet, TcpTableClass.TcpTableOwnerPidAll, 0);
        var buffer = Marshal.AllocHGlobal(bufferLength);

        try
        {
            var result = GetExtendedTcpTable(buffer, ref bufferLength, true, AfInet, TcpTableClass.TcpTableOwnerPidAll, 0);
            if (result != 0)
            {
                yield break;
            }

            var rowCount = Marshal.ReadInt32(buffer);
            var rowPtr = IntPtr.Add(buffer, 4);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();

            for (var i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                yield return new TcpRow(
                    row.OwningPid,
                    new IPAddress(row.LocalAddr).ToString(),
                    ConvertPort(row.LocalPort),
                    new IPAddress(row.RemoteAddr).ToString(),
                    ConvertPort(row.RemotePort),
                    ((TcpState)row.State).ToString());
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int ConvertPort(uint port) => (int)(((port & 0xFF) << 8) + ((port & 0xFF00) >> 8));

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int tcpTableLength,
        bool sort,
        int ipVersion,
        TcpTableClass tableClass,
        uint reserved);

    private enum TcpTableClass
    {
        TcpTableBasicListener,
        TcpTableBasicConnections,
        TcpTableBasicAll,
        TcpTableOwnerPidListener,
        TcpTableOwnerPidConnections,
        TcpTableOwnerPidAll
    }

    private enum TcpState
    {
        Closed = 1,
        Listen = 2,
        SynSent = 3,
        SynReceived = 4,
        Established = 5,
        FinWait1 = 6,
        FinWait2 = 7,
        CloseWait = 8,
        Closing = 9,
        LastAck = 10,
        TimeWait = 11,
        DeleteTcb = 12
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public int OwningPid;
    }
}

internal sealed record TcpRow(int ProcessId, string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort, string State);

internal static class AppCatalog
{
    private static readonly string[] ExecutableExtensions = [".exe", ".cmd", ".bat"];

    public static string? Resolve(string query)
    {
        query = query.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        if (File.Exists(query))
        {
            return Path.GetFullPath(query);
        }

        if (Path.GetExtension(query).Length == 0)
        {
            foreach (var extension in ExecutableExtensions)
            {
                var withExtension = query + extension;
                if (File.Exists(withExtension))
                {
                    return Path.GetFullPath(withExtension);
                }
            }
        }

        var exact = Find(query)
            .FirstOrDefault(app =>
                string.Equals(app.Name, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(app.Path), query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(app.Path), query, StringComparison.OrdinalIgnoreCase));

        return exact?.Path ?? Find(query).FirstOrDefault()?.Path;
    }

    public static IEnumerable<AppEntry> Find(string? query = null)
    {
        var entries = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in ReadKnownApps().Concat(ReadAppPaths()).Concat(ReadPathExecutables()).Concat(ReadStartMenuShortcuts()))
        {
            if (string.IsNullOrWhiteSpace(app.Path) || !File.Exists(app.Path))
            {
                continue;
            }

            entries.TryAdd(app.Path, app);
        }

        IEnumerable<AppEntry> filtered = entries.Values;
        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(app =>
                app.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(app.Path).Contains(query, StringComparison.OrdinalIgnoreCase)
                || app.Path.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        return filtered
            .OrderBy(app => Score(app, query))
            .ThenBy(app => app.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static int Score(AppEntry app, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return app.Source switch
            {
                "known" => 0,
                "app-paths" => 1,
                "start-menu" => 2,
                "path" => 3,
                _ => 4
            };
        }

        if (string.Equals(app.Name, query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(Path.GetFileNameWithoutExtension(app.Path), query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (app.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (Path.GetFileNameWithoutExtension(app.Path).StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 3;
        return 4;
    }

    private static IEnumerable<AppEntry> ReadKnownApps()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var candidates = new[]
        {
            new AppEntry("code", Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe"), "known"),
            new AppEntry("cursor", Path.Combine(localAppData, "Programs", "Cursor", "Cursor.exe"), "known"),
            new AppEntry("chrome", Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"), "known"),
            new AppEntry("notepad", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe"), "known")
        };

        foreach (var candidate in candidates)
        {
            yield return candidate;
        }
    }

    private static IEnumerable<AppEntry> ReadAppPaths()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var appPaths = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
                if (appPaths is null)
                {
                    continue;
                }

                foreach (var subKeyName in appPaths.GetSubKeyNames())
                {
                    using var subKey = appPaths.OpenSubKey(subKeyName);
                    var path = Convert.ToString(subKey?.GetValue(null), CultureInfo.InvariantCulture);
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    yield return new AppEntry(Path.GetFileNameWithoutExtension(subKeyName), path.Trim('"'), "app-paths");
                }
            }
        }
    }

    private static IEnumerable<AppEntry> ReadPathExecutables()
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var folder in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.exe");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return new AppEntry(Path.GetFileNameWithoutExtension(file), file, "path");
            }
        }
    }

    private static IEnumerable<AppEntry> ReadStartMenuShortcuts()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> shortcuts;
            try
            {
                shortcuts = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                continue;
            }

            foreach (var shortcut in shortcuts)
            {
                var target = ShortcutResolver.GetTarget(shortcut);
                if (string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                yield return new AppEntry(Path.GetFileNameWithoutExtension(shortcut), target, "start-menu");
            }
        }
    }
}

internal sealed record AppEntry(string Name, string Path, string Source);

internal static class ShortcutResolver
{
    public static string? GetTarget(string shortcutPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            var shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            var shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
            var target = shortcut?.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null);
            return Convert.ToString(target, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            return null;
        }
    }
}

internal static class RegistrySnapshot
{
    private static readonly RegistryLocation[] Locations =
    [
        new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
        new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
        new(RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run"),
        new(RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\RunOnce")
    ];

    public static Dictionary<string, string?> Capture()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in Locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(location.Hive, RegistryView.Default);
                using var key = baseKey.OpenSubKey(location.Path);
                if (key is null)
                {
                    continue;
                }

                foreach (var name in key.GetValueNames())
                {
                    values[$"{location.Hive}\\{location.Path}\\{name}"] = Convert.ToString(key.GetValue(name), CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                values[$"{location.Hive}\\{location.Path}\\<error>"] = ex.Message;
            }
        }

        return values;
    }

    public static List<RegistryEvent> Compare(Dictionary<string, string?> before, Dictionary<string, string?> after)
    {
        var events = new List<RegistryEvent>();
        foreach (var (key, value) in after)
        {
            if (!before.TryGetValue(key, out var previous))
            {
                events.Add(new RegistryEvent(RegistryEventKind.Created, key, null, value));
            }
            else if (!string.Equals(previous, value, StringComparison.Ordinal))
            {
                events.Add(new RegistryEvent(RegistryEventKind.Modified, key, previous, value));
            }
        }

        foreach (var (key, value) in before)
        {
            if (!after.ContainsKey(key))
            {
                events.Add(new RegistryEvent(RegistryEventKind.Deleted, key, value, null));
            }
        }

        return events;
    }

    private sealed record RegistryLocation(RegistryHive Hive, string Path);
}

internal sealed record RegistryEvent(RegistryEventKind Kind, string Key, string? Before, string? After);

internal enum RegistryEventKind
{
    Created,
    Modified,
    Deleted
}

internal static class PathClassifier
{
    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private static readonly string Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    private static readonly string Downloads = Path.Combine(UserProfile, "Downloads");

    public static string Classify(string path)
    {
        if (IsSensitive(path)) return "sensitive";
        if (IsUnder(path, Path.GetTempPath())) return "temp";
        if (IsUnder(path, AppData) || IsUnder(path, LocalAppData)) return "app-data";
        if (IsUnder(path, Downloads)) return "downloads";
        if (IsUnder(path, Documents)) return "documents";
        if (IsUnder(path, Desktop)) return "desktop";
        if (IsUnder(path, Windows)) return "system";
        if (IsUnder(path, ProgramFiles) || IsUnder(path, ProgramFilesX86)) return "program-files";
        if (path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) return "dependencies";
        if (path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) return "git";
        return "project-or-user";
    }

    public static bool IsSensitive(string path)
    {
        var normalized = path.Replace('/', '\\');
        var fileName = Path.GetFileName(normalized);
        return normalized.Contains("\\.ssh\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\.aws\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\.azure\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\.gnupg\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\.kube\\", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".env", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".npmrc", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".pypirc", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".gitconfig", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("id_rsa", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("credentials", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("token", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUsuallyNoise(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        var fullRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record SessionReport(
    string App,
    string Arguments,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    IReadOnlyList<string> WatchRoots,
    SessionSummary Summary,
    IReadOnlyList<FileEvent> FileEvents,
    IReadOnlyList<ProcessRecord> Processes,
    IReadOnlyList<NetworkEvent> NetworkEvents,
    IReadOnlyList<RegistryEvent> RegistryEvents,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<FolderImpact> TopFolders,
    IReadOnlyList<string> SnapshotErrors)
{
    public static SessionReport Build(
        string app,
        string arguments,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        IReadOnlyList<string> watchRoots,
        FileSnapshot before,
        FileSnapshot after,
        IReadOnlyList<FileEvent> fileEvents,
        IReadOnlyList<ProcessRecord> processes,
        IReadOnlyList<NetworkEvent> networkEvents,
        IReadOnlyList<RegistryEvent> registryEvents)
    {
        var topFolders = BuildFolderImpact(fileEvents);
        var findings = Analyzer.Find(fileEvents, processes, networkEvents, registryEvents, topFolders);
        var summary = new SessionSummary(
            fileEvents.Count(e => e.Kind == FileEventKind.Created),
            fileEvents.Count(e => e.Kind == FileEventKind.Modified),
            fileEvents.Count(e => e.Kind == FileEventKind.Deleted),
            fileEvents.Where(e => e.Kind != FileEventKind.Deleted).Sum(e => Math.Max(0, e.SizeDelta)),
            processes.Count,
            processes.Count(p => !string.IsNullOrWhiteSpace(p.CommandLine)),
            networkEvents.Count,
            registryEvents.Count,
            findings.Count(f => f.Severity == "high" || f.Severity == "medium"));

        return new SessionReport(
            app,
            arguments,
            startedAt,
            endedAt,
            watchRoots,
            summary,
            fileEvents,
            processes,
            networkEvents,
            registryEvents,
            findings,
            topFolders,
            before.Errors.Concat(after.Errors).Distinct().Take(200).ToList());
    }

    private static List<FolderImpact> BuildFolderImpact(IReadOnlyList<FileEvent> events)
    {
        return events
            .Where(e => e.Kind is FileEventKind.Created or FileEventKind.Modified)
            .GroupBy(e => Path.GetDirectoryName(e.Path) ?? e.Path, StringComparer.OrdinalIgnoreCase)
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
    int FilesCreated,
    int FilesModified,
    int FilesDeleted,
    long BytesAddedOrChanged,
    int ProcessCount,
    int CommandCount,
    int NetworkConnectionCount,
    int RegistryChangeCount,
    int RiskObservationCount);

internal sealed record FolderImpact(string Path, int FileCount, long BytesAdded, string Category);

internal sealed record Finding(string Severity, string Title, string Detail);

internal static class Analyzer
{
    public static List<Finding> Find(
        IReadOnlyList<FileEvent> files,
        IReadOnlyList<ProcessRecord> processes,
        IReadOnlyList<NetworkEvent> network,
        IReadOnlyList<RegistryEvent> registry,
        IReadOnlyList<FolderImpact> topFolders)
    {
        var findings = new List<Finding>();

        foreach (var file in files.Where(f => f.IsSensitive).Take(20))
        {
            findings.Add(new Finding("medium", "Sensitive path changed", $"{file.Kind} {file.Path}"));
        }

        foreach (var process in processes.Where(p => p.CommandLine?.Contains("ExecutionPolicy Bypass", StringComparison.OrdinalIgnoreCase) == true))
        {
            findings.Add(new Finding("medium", "PowerShell execution policy bypass", process.CommandLine ?? process.Name));
        }

        foreach (var process in processes.Where(p => IsPackageInstall(p.CommandLine)).Take(10))
        {
            findings.Add(new Finding("info", "Package install command", process.CommandLine ?? process.Name));
        }

        foreach (var entry in registry.Where(r => r.Key.Contains("\\Run", StringComparison.OrdinalIgnoreCase)))
        {
            findings.Add(new Finding("high", "Startup persistence registry change", $"{entry.Kind}: {entry.Key}"));
        }

        foreach (var folder in topFolders.Where(f => f.Category is "temp" or "app-data" && f.BytesAdded >= 100 * 1024 * 1024).Take(10))
        {
            findings.Add(new Finding("info", "Large cache or temp growth", $"{Format.Bytes(folder.BytesAdded)} added under {folder.Path}"));
        }

        if (network.Count > 25)
        {
            findings.Add(new Finding("info", "Many network endpoints", $"{network.Count} distinct IPv4 TCP endpoints were observed."));
        }

        return findings;
    }

    private static bool IsPackageInstall(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        return commandLine.Contains("npm install", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("pnpm add", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("yarn add", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("pip install", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("uv add", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class HtmlReport
{
    public static string Render(SessionReport session)
    {
        var appName = WebUtility.HtmlEncode(Path.GetFileName(session.App));
        var rows = string.Join(Environment.NewLine, session.FileEvents.Take(200).Select(RenderFileRow));
        var processes = string.Join(Environment.NewLine, session.Processes.Select(p => $"<tr><td>{p.ProcessId}</td><td>{Esc(p.Name)}</td><td>{Esc(p.CommandLine ?? "")}</td></tr>"));
        var network = string.Join(Environment.NewLine, session.NetworkEvents.Take(200).Select(n => $"<tr><td>{n.ProcessId}</td><td>{Esc(n.RemoteAddress)}:{n.RemotePort}</td><td>{Esc(n.State)}</td></tr>"));
        var findings = string.Join(Environment.NewLine, session.Findings.Select(f => $"<li class=\"{Esc(f.Severity)}\"><strong>{Esc(f.Severity.ToUpperInvariant())}</strong> {Esc(f.Title)}<br><span>{Esc(f.Detail)}</span></li>"));
        var folders = string.Join(Environment.NewLine, session.TopFolders.Select(f => $"<tr><td>{Esc(f.Path)}</td><td>{Esc(f.Category)}</td><td>{f.FileCount:N0}</td><td>{Format.Bytes(f.BytesAdded)}</td></tr>"));

        return $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>AppLedger Report - {{appName}}</title>
          <style>
            :root { color-scheme: light; --ink:#17202a; --muted:#627083; --line:#d8dee7; --bg:#f6f8fb; --panel:#fff; --accent:#1664d9; --warn:#b25b00; --bad:#a11919; }
            * { box-sizing:border-box; }
            body { margin:0; font-family:Segoe UI, Arial, sans-serif; color:var(--ink); background:var(--bg); }
            header { background:#101820; color:#fff; padding:32px 40px; }
            header p { color:#c7d1dc; margin:8px 0 0; }
            main { max-width:1180px; margin:0 auto; padding:28px 24px 60px; }
            section { margin:22px 0; }
            .grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(180px,1fr)); gap:12px; }
            .metric { background:var(--panel); border:1px solid var(--line); border-radius:8px; padding:16px; }
            .metric strong { display:block; font-size:28px; }
            .metric span { color:var(--muted); font-size:13px; }
            .panel { background:var(--panel); border:1px solid var(--line); border-radius:8px; overflow:hidden; }
            h2 { margin:0 0 12px; font-size:20px; }
            table { width:100%; border-collapse:collapse; font-size:13px; }
            th, td { padding:10px 12px; border-bottom:1px solid var(--line); text-align:left; vertical-align:top; }
            th { background:#eef2f7; color:#364454; font-weight:600; }
            code { font-family:Cascadia Mono, Consolas, monospace; font-size:12px; }
            ul.findings { list-style:none; padding:0; margin:0; }
            ul.findings li { background:var(--panel); border:1px solid var(--line); border-left:5px solid var(--accent); border-radius:8px; margin:10px 0; padding:12px 14px; }
            ul.findings li.high { border-left-color:var(--bad); }
            ul.findings li.medium { border-left-color:var(--warn); }
            ul.findings span { color:var(--muted); }
            .muted { color:var(--muted); }
          </style>
        </head>
        <body>
          <header>
            <h1>AppLedger Report: {{appName}}</h1>
            <p>{{Esc(session.StartedAt.ToString("g"))}} - {{Esc(session.EndedAt.ToString("g"))}} · {{Esc(string.Join("; ", session.WatchRoots))}}</p>
          </header>
          <main>
            <section class="grid">
              <div class="metric"><strong>{{session.Summary.FilesCreated:N0}}</strong><span>files created</span></div>
              <div class="metric"><strong>{{session.Summary.FilesModified:N0}}</strong><span>files modified</span></div>
              <div class="metric"><strong>{{session.Summary.FilesDeleted:N0}}</strong><span>files deleted</span></div>
              <div class="metric"><strong>{{Format.Bytes(session.Summary.BytesAddedOrChanged)}}</strong><span>added or changed</span></div>
              <div class="metric"><strong>{{session.Summary.CommandCount:N0}}</strong><span>commands captured</span></div>
              <div class="metric"><strong>{{session.Summary.NetworkConnectionCount:N0}}</strong><span>network endpoints</span></div>
            </section>

            <section>
              <h2>Risky Observations</h2>
              {{(session.Findings.Count == 0 ? "<p class=\"muted\">No risky observations from the v0 analyzers.</p>" : $"<ul class=\"findings\">{findings}</ul>")}}
            </section>

            <section>
              <h2>Top Folders Touched</h2>
              <div class="panel"><table><thead><tr><th>Folder</th><th>Category</th><th>Files</th><th>Growth</th></tr></thead><tbody>{{folders}}</tbody></table></div>
            </section>

            <section>
              <h2>Files</h2>
              <div class="panel"><table><thead><tr><th>Action</th><th>Category</th><th>Delta</th><th>Path</th></tr></thead><tbody>{{rows}}</tbody></table></div>
            </section>

            <section>
              <h2>Child Processes and Commands</h2>
              <div class="panel"><table><thead><tr><th>PID</th><th>Name</th><th>Command</th></tr></thead><tbody>{{processes}}</tbody></table></div>
            </section>

            <section>
              <h2>Network</h2>
              <div class="panel"><table><thead><tr><th>PID</th><th>Remote</th><th>State</th></tr></thead><tbody>{{network}}</tbody></table></div>
            </section>

            <p class="muted">v0 uses live process/network sampling plus before/after file snapshots. File reads and packet contents are not collected.</p>
          </main>
        </body>
        </html>
        """;
    }

    private static string RenderFileRow(FileEvent file) =>
        $"<tr><td>{Esc(file.Kind.ToString())}</td><td>{Esc(file.Category)}</td><td>{Format.Bytes(file.SizeDelta)}</td><td><code>{Esc(file.Path)}</code></td></tr>";

    private static string Esc(string value) => WebUtility.HtmlEncode(value);
}

internal static class CsvReport
{
    public static string RenderFiles(IReadOnlyList<FileEvent> events)
    {
        var builder = new StringBuilder();
        builder.AppendLine("kind,category,size_before,size_after,size_delta,is_sensitive,path");
        foreach (var item in events)
        {
            builder.AppendLine(string.Join(",", [
                Csv(item.Kind.ToString()),
                Csv(item.Category),
                Csv(item.SizeBefore?.ToString(CultureInfo.InvariantCulture) ?? ""),
                Csv(item.SizeAfter?.ToString(CultureInfo.InvariantCulture) ?? ""),
                Csv(item.SizeDelta.ToString(CultureInfo.InvariantCulture)),
                Csv(item.IsSensitive.ToString(CultureInfo.InvariantCulture)),
                Csv(item.Path)
            ]));
        }

        return builder.ToString();
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}

internal static class CleanupScript
{
    public static string Render(SessionReport session)
    {
        var candidates = session.TopFolders
            .Where(f => f.Category is "temp" or "app-data" && f.BytesAdded > 0)
            .Take(20)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine("# AppLedger conservative cleanup draft");
        builder.AppendLine("# Review every path before uncommenting. AppLedger does not delete anything automatically.");
        builder.AppendLine();

        foreach (var folder in candidates)
        {
            builder.AppendLine($"# {Format.Bytes(folder.BytesAdded)} observed under {folder.Path}");
            builder.AppendLine($"# Remove-Item -LiteralPath '{folder.Path.Replace("'", "''", StringComparison.Ordinal)}' -Recurse -Force");
            builder.AppendLine();
        }

        return builder.ToString();
    }
}

internal static class Format
{
    public static string Bytes(long bytes)
    {
        var sign = bytes < 0 ? "-" : "";
        double value = Math.Abs(bytes);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{sign}{value:0.#} {units[unit]}";
    }
}
