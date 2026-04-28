using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Win32;

[assembly: InternalsVisibleTo("AppLedger.Tests")]

namespace AppLedger;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
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
                "attach" => await AttachAsync(args.Skip(1).ToArray()),
                "apps" => Apps(args.Skip(1).ToArray()),
                "processes" or "ps" => Processes(args.Skip(1).ToArray()),
                "report" => await ReportAsync(args.Skip(1).ToArray()),
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
        Console.WriteLine($"  Watch:     {DescribeWatchScope(options.WatchRoots, options.WatchAll)}");
        Console.WriteLine();

        Console.WriteLine("Taking before snapshot...");
        var before = FileSnapshot.Capture(options.WatchRoots);
        var registryBefore = RegistrySnapshot.Capture();

        using var stop = new CancellationTokenSource();
        using var etwCollector = EtwCollector.TryStart(options.WatchRoots, options.WatchAll, options.CaptureReads, options.MaxEvents);
        if (etwCollector.IsRunning)
        {
            Console.WriteLine("ETW:       live kernel file/process capture enabled");
        }
        else if (!string.IsNullOrWhiteSpace(etwCollector.Status))
        {
            Console.WriteLine($"ETW:       unavailable ({etwCollector.Status})");
        }

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
        etwCollector.AttachSession(process.Id, processSampler);
        var networkSampler = new NetworkSampler();

        Console.WriteLine($"Recording PID {process.Id}. Press Ctrl+C to stop.");
        var emptyTreeSamples = 0;

        while (!stop.IsCancellationRequested)
        {
            processSampler.Sample();
            etwCollector.SyncSessionProcesses(processSampler.SessionProcessIds);
            networkSampler.Sample(processSampler.SessionProcessIds);

            if (etwCollector.HasReachedEventLimit)
            {
                Console.WriteLine($"Reached event limit ({options.MaxEvents:N0}). Stopping recording...");
                break;
            }

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
        etwCollector.Stop();
        Console.WriteLine("Taking after snapshot...");
        var after = FileSnapshot.Capture(options.WatchRoots);
        var registryAfter = RegistrySnapshot.Capture();

        var fileEvents = FileEventMerger.Merge(etwCollector.FileEvents, FileDiff.Compare(before, after));
        processSampler.Merge(etwCollector.Processes);
        var registryEvents = RegistrySnapshot.Compare(registryBefore, registryAfter);
        var session = SessionReport.Build(
            options.Target,
            options.AppArguments,
            startedAt,
            endedAt,
            options.WatchRoots,
            options.WatchAll,
            before,
            after,
            fileEvents,
            processSampler.Processes.Values.OrderBy(p => p.FirstSeen).ToList(),
            networkSampler.Events.OrderBy(e => e.FirstSeen).ToList(),
            registryEvents);

        var outputs = (await SessionOutputs.WriteAsync(session, options.OutputDirectory, JsonOptions)).ToList();
        if (options.WriteSqlite)
        {
            var sqlitePath = Path.Combine(options.OutputDirectory, "session.sqlite");
            SessionStore.Write(sqlitePath, session);
            outputs.Add(sqlitePath);
        }

        Console.WriteLine();
        Console.WriteLine($"AppLedger Report: {Path.GetFileNameWithoutExtension(options.Target)}");
        Console.WriteLine($"  Files created:  {session.Summary.FilesCreated}");
        Console.WriteLine($"  Files modified: {session.Summary.FilesModified}");
        Console.WriteLine($"  Files deleted:  {session.Summary.FilesDeleted}");
        Console.WriteLine($"  File reads:     {session.Summary.FilesRead}");
        Console.WriteLine($"  File renames:   {session.Summary.FilesRenamed}");
        Console.WriteLine($"  Processes:      {session.Processes.Count}");
        Console.WriteLine($"  Commands:       {session.Processes.Count(p => !string.IsNullOrWhiteSpace(p.CommandLine))}");
        Console.WriteLine($"  Connections:    {session.NetworkEvents.Count}");
        Console.WriteLine($"  Findings:       {session.Findings.Count}");
        Console.WriteLine();
        Console.WriteLine("Generated:");
        foreach (var output in outputs)
        {
            Console.WriteLine($"  {output}");
        }

        return 0;
    }

    private static async Task<int> AttachAsync(string[] args)
    {
        var options = AttachOptions.Parse(args);
        if (options is null)
        {
            return 1;
        }

        Directory.CreateDirectory(options.OutputDirectory);

        Console.WriteLine("AppLedger attach recorder");
        Console.WriteLine($"  PID:       {options.Root.ProcessId}");
        Console.WriteLine($"  App:       {options.Root.ExecutablePath ?? options.Root.Name}");
        Console.WriteLine($"  Output:    {options.OutputDirectory}");
        Console.WriteLine($"  Watch:     {DescribeWatchScope(options.WatchRoots, options.WatchAll)}");
        Console.WriteLine();

        Console.WriteLine("Taking before snapshot...");
        var before = FileSnapshot.Capture(options.WatchRoots);
        var registryBefore = RegistrySnapshot.Capture();

        using var stop = new CancellationTokenSource();
        using var etwCollector = EtwCollector.TryStart(options.WatchRoots, options.WatchAll, options.CaptureReads, options.MaxEvents);
        if (etwCollector.IsRunning)
        {
            Console.WriteLine("ETW:       live kernel file/process capture enabled");
        }
        else if (!string.IsNullOrWhiteSpace(etwCollector.Status))
        {
            Console.WriteLine($"ETW:       unavailable ({etwCollector.Status})");
        }

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

        var startedAt = DateTimeOffset.Now;
        var processSampler = new ProcessSampler(
            options.Root.ProcessId,
            options.Root.ExecutablePath ?? options.Root.Name,
            options.Root.CommandLine ?? "");
        processSampler.Observe(options.Root);
        processSampler.Sample();
        etwCollector.AttachSession(options.Root.ProcessId, processSampler);
        var networkSampler = new NetworkSampler();

        Console.WriteLine($"Recording existing PID {options.Root.ProcessId}. Press Ctrl+C to stop.");
        var emptyTreeSamples = 0;

        while (!stop.IsCancellationRequested)
        {
            processSampler.Sample();
            etwCollector.SyncSessionProcesses(processSampler.SessionProcessIds);
            networkSampler.Sample(processSampler.SessionProcessIds);

            if (etwCollector.HasReachedEventLimit)
            {
                Console.WriteLine($"Reached event limit ({options.MaxEvents:N0}). Stopping recording...");
                break;
            }

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
        etwCollector.Stop();
        Console.WriteLine("Taking after snapshot...");
        var after = FileSnapshot.Capture(options.WatchRoots);
        var registryAfter = RegistrySnapshot.Capture();

        var fileEvents = FileEventMerger.Merge(etwCollector.FileEvents, FileDiff.Compare(before, after));
        processSampler.Merge(etwCollector.Processes);
        var registryEvents = RegistrySnapshot.Compare(registryBefore, registryAfter);
        var session = SessionReport.Build(
            options.Root.ExecutablePath ?? options.Root.Name,
            options.Root.CommandLine ?? "",
            startedAt,
            endedAt,
            options.WatchRoots,
            options.WatchAll,
            before,
            after,
            fileEvents,
            processSampler.Processes.Values.OrderBy(p => p.FirstSeen).ToList(),
            networkSampler.Events.OrderBy(e => e.FirstSeen).ToList(),
            registryEvents);

        var outputs = (await SessionOutputs.WriteAsync(session, options.OutputDirectory, JsonOptions)).ToList();
        if (options.WriteSqlite)
        {
            var sqlitePath = Path.Combine(options.OutputDirectory, "session.sqlite");
            SessionStore.Write(sqlitePath, session);
            outputs.Add(sqlitePath);
        }

        Console.WriteLine();
        Console.WriteLine($"AppLedger Report: {options.Root.Name} ({options.Root.ProcessId})");
        Console.WriteLine($"  Files created:  {session.Summary.FilesCreated}");
        Console.WriteLine($"  Files modified: {session.Summary.FilesModified}");
        Console.WriteLine($"  Files deleted:  {session.Summary.FilesDeleted}");
        Console.WriteLine($"  File reads:     {session.Summary.FilesRead}");
        Console.WriteLine($"  File renames:   {session.Summary.FilesRenamed}");
        Console.WriteLine($"  Processes:      {session.Processes.Count}");
        Console.WriteLine($"  Commands:       {session.Processes.Count(p => !string.IsNullOrWhiteSpace(p.CommandLine))}");
        Console.WriteLine($"  Connections:    {session.NetworkEvents.Count}");
        Console.WriteLine($"  Findings:       {session.Findings.Count}");
        Console.WriteLine();
        Console.WriteLine("Generated:");
        foreach (var output in outputs)
        {
            Console.WriteLine($"  {output}");
        }

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

    private static int Processes(string[] args)
    {
        var query = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
        var processes = ProcessCatalog.Find(query)
            .Take(100)
            .ToList();

        if (processes.Count == 0)
        {
            Console.WriteLine(string.IsNullOrWhiteSpace(query)
                ? "No processes found."
                : $"No processes found for '{query}'.");
            return 0;
        }

        foreach (var process in processes)
        {
            Console.WriteLine($"{process.ProcessId,7} {process.ParentProcessId,7} {process.Name,-24} {process.ExecutablePath ?? process.CommandLine}");
        }

        return 0;
    }

    private static async Task<int> ReportAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return Fail("Usage: appledger report <session.json|session.sqlite> [--out <dir>]");
        }

        var input = Path.GetFullPath(Environment.ExpandEnvironmentVariables(args[0].Trim('"')));
        if (!File.Exists(input))
        {
            return Fail($"Session file does not exist: {input}");
        }

        var session = Path.GetExtension(input).Equals(".sqlite", StringComparison.OrdinalIgnoreCase)
            ? SessionStore.Read(input)
            : JsonSerializer.Deserialize<SessionReport>(File.ReadAllText(input), JsonOptions);
        if (session is null)
        {
            return Fail($"Could not read session: {input}");
        }
        session = SessionReport.RefreshDerivedData(session);

        var output = Cli.GetOption(args, "--out")
            ?? Path.Combine(Path.GetDirectoryName(input) ?? Directory.GetCurrentDirectory(), "regenerated-report");
        output = Path.GetFullPath(Environment.ExpandEnvironmentVariables(output));

        var outputs = (await SessionOutputs.WriteAsync(session, output, JsonOptions)).ToList();
        if (!Cli.HasFlag(args, "--no-sqlite"))
        {
            var sqlitePath = Path.Combine(output, "session.sqlite");
            SessionStore.Write(sqlitePath, session);
            outputs.Add(sqlitePath);
        }
        Console.WriteLine("Generated:");
        foreach (var path in outputs)
        {
            Console.WriteLine($"  {path}");
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

    private static string DescribeWatchScope(IReadOnlyList<string> watchRoots, bool watchAll) =>
        watchAll
            ? (watchRoots.Count == 0
                ? "[all live file paths]"
                : $"[all live file paths] + snapshots under {string.Join("; ", watchRoots)}")
            : string.Join("; ", watchRoots);

    private static void PrintHelp()
    {
        Console.WriteLine("""
        AppLedger - a black box recorder for Windows apps.

        Usage:
          appledger apps [search]
          appledger ps [search]
          appledger run <app name|alias|exe> [--args "<arguments>"] [--watch <path>] [--watch-all] [--no-reads] [--max-events <n>] [--no-sqlite] [--out <dir>] [--timeout <seconds>]
          appledger attach <pid|process search> [--watch <path>] [--watch-all] [--no-reads] [--max-events <n>] [--no-sqlite] [--out <dir>] [--timeout <seconds>]
          appledger report <session.json|session.sqlite> [--out <dir>] [--no-sqlite]
          appledger snapshot <output.json> --watch <path>
          appledger diff <before.json> <after.json>

        Examples:
          appledger apps code
          appledger run code --watch "C:\Users\Anas\Projects\demo-app"
          appledger ps codex
          appledger attach 20376 --watch "C:\Users\Anas\Documents\New project 8" --out artifacts\codex-self --timeout 300
          appledger attach 20376 --watch-all --out artifacts\codex-full
          appledger attach 20376 --watch-all --no-reads --max-events 50000 --out artifacts\codex-full
          appledger run "C:\Windows\System32\notepad.exe" --watch "%USERPROFILE%\Documents"
          appledger run "C:\Path\To\Code.exe" --watch "C:\Users\Anas\Projects\demo-app"

        Notes:
          Phase 1 uses live ETW file/process capture when elevated, samples IPv4 TCP
          connections, and keeps before/after snapshots as a fallback.
        """);
    }
}

internal sealed record RunOptions(
    string Target,
    string AppArguments,
    string WorkingDirectory,
    string OutputDirectory,
    IReadOnlyList<string> WatchRoots,
    bool WatchAll,
    bool CaptureReads,
    int? MaxEvents,
    bool WriteSqlite,
    TimeSpan? Timeout)
{
    public static RunOptions? Parse(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: appledger run <app name|alias|exe> [--args \"...\"] [--watch <path>] [--watch-all] [--no-reads] [--max-events <n>] [--no-sqlite] [--out <dir>]");
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
        var watchAll = Cli.HasFlag(args, "--watch-all");
        var captureReads = !Cli.HasFlag(args, "--no-reads");
        var writeSqlite = !Cli.HasFlag(args, "--no-sqlite");
        var maxEvents = Cli.GetPositiveIntOption(args, "--max-events");

        if (watchRoots.Count == 0 && !watchAll)
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
            watchAll,
            captureReads,
            maxEvents,
            writeSqlite,
            timeout);
    }
}

internal sealed record AttachOptions(
    ProcessRecord Root,
    string OutputDirectory,
    IReadOnlyList<string> WatchRoots,
    bool WatchAll,
    bool CaptureReads,
    int? MaxEvents,
    bool WriteSqlite,
    TimeSpan? Timeout)
{
    public static AttachOptions? Parse(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: appledger attach <pid|process search> [--watch <path>] [--watch-all] [--no-reads] [--max-events <n>] [--no-sqlite] [--out <dir>] [--timeout <seconds>]");
            return null;
        }

        var query = args[0].Trim('"');
        var root = ProcessCatalog.Resolve(query);
        if (root is null)
        {
            Console.Error.WriteLine($"Could not resolve running process: {query}");
            Console.Error.WriteLine($"Try: appledger ps \"{query}\"");
            return null;
        }

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
        var watchAll = Cli.HasFlag(args, "--watch-all");
        var captureReads = !Cli.HasFlag(args, "--no-reads");
        var writeSqlite = !Cli.HasFlag(args, "--no-sqlite");
        var maxEvents = Cli.GetPositiveIntOption(args, "--max-events");

        if (watchRoots.Count == 0 && !watchAll)
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

        return new AttachOptions(root, output, watchRoots, watchAll, captureReads, maxEvents, writeSqlite, timeout);
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

    public static bool HasFlag(IReadOnlyList<string> args, string name) =>
        args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    public static int? GetPositiveIntOption(IReadOnlyList<string> args, string name)
    {
        var value = GetOption(args, name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;
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
    DateTimeOffset LastSeen);

internal sealed class EtwCollector : IDisposable
{
    private readonly IReadOnlyList<string> _watchRoots;
    private readonly bool _watchAll;
    private readonly bool _captureReads;
    private readonly int? _maxEvents;
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

    private EtwCollector(IReadOnlyList<string> watchRoots, bool watchAll, bool captureReads, int? maxEvents, TraceEventSession? session, Task? processingTask, string? status)
    {
        _watchRoots = watchRoots;
        _watchAll = watchAll;
        _captureReads = captureReads;
        _maxEvents = maxEvents;
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
    {
        if (TraceEventSession.IsElevated() != true)
        {
            return new EtwCollector(watchRoots, watchAll, captureReads, maxEvents, null, null, "run from an elevated terminal for live ETW file events");
        }

        try
        {
            var session = new TraceEventSession(KernelTraceEventParser.KernelSessionName) { StopOnDispose = true };
            var collector = new EtwCollector(watchRoots, watchAll, captureReads, maxEvents, session, null, null);
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
            return new EtwCollector(watchRoots, watchAll, captureReads, maxEvents, null, null, ex.Message);
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

            if (_sessionPids.ContainsKey(data.ParentID))
            {
                _sessionPids[data.ProcessID] = 0;
                _processes[data.ProcessID] = record;
                _processSampler?.Observe(record);
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
            AddFile(FileEventKind.Renamed, pending.FromPath, pending.ProcessId, pending.ProcessName, data.TimeStamp, data.FileName);
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
            AddFile(FileEventKind.Renamed, currentPath, data.ProcessID, data.ProcessName, data.TimeStamp, data.FileName);
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
                    DateTimeOffset.Now,
                    null));
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
    DateTimeOffset FirstSeen,
    string? RemoteHost);

internal static class NetworkResolver
{
    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<Dictionary<string, string>> DnsCacheIndex = new(BuildDnsCacheIndex, true);

    public static List<NetworkEvent> Enrich(IReadOnlyList<NetworkEvent> events)
    {
        if (events.Count == 0)
        {
            return [];
        }

        var resolved = events
            .Select(item => item with
            {
                RemoteHost = ResolveHost(item.RemoteAddress)
            })
            .ToList();

        return resolved;
    }

    public static string DisplayHost(NetworkEvent item)
    {
        if (!string.IsNullOrWhiteSpace(item.RemoteHost))
        {
            return item.RemoteHost;
        }

        return item.RemoteAddress;
    }

    private static string? ResolveHost(string remoteAddress)
    {
        if (string.IsNullOrWhiteSpace(remoteAddress))
        {
            return null;
        }

        if (!IPAddress.TryParse(remoteAddress, out var ip))
        {
            return remoteAddress;
        }

        if (DnsCacheIndex.Value.TryGetValue(remoteAddress, out var cachedHost))
        {
            Cache[remoteAddress] = cachedHost;
            return cachedHost;
        }

        if (Cache.TryGetValue(remoteAddress, out var cached))
        {
            return cached;
        }

        try
        {
            var lookup = Dns.GetHostEntryAsync(remoteAddress);
            if (!lookup.Wait(TimeSpan.FromMilliseconds(500)))
            {
                Cache[remoteAddress] = null;
                return null;
            }

            var entry = lookup.GetAwaiter().GetResult();
            var host = NormalizeHost(entry.HostName);
            Cache[remoteAddress] = host;
            return host;
        }
        catch
        {
            Cache[remoteAddress] = null;
            return null;
        }
    }

    private static string? NormalizeHost(string? hostName)
    {
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return null;
        }

        return hostName.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static Dictionary<string, string> BuildDnsCacheIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\StandardCimv2",
                "SELECT Entry, Data, Type FROM MSFT_DNSClientCache");

            foreach (ManagementObject entry in searcher.Get())
            {
                var type = Convert.ToInt32(entry["Type"] ?? 0, CultureInfo.InvariantCulture);
                var recordName = entry["Entry"]?.ToString();
                var data = entry["Data"]?.ToString();

                if (type == 1 && IPAddress.TryParse(data, out _))
                {
                    RememberHost(index, data!, recordName);
                }
                else if (type == 12 && TryReversePointerToIpv4(recordName, out var address))
                {
                    RememberHost(index, address, data);
                }
            }
        }
        catch
        {
            return index;
        }

        return index;
    }

    private static void RememberHost(Dictionary<string, string> index, string address, string? host)
    {
        var normalized = NormalizeHost(host);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (!index.TryGetValue(address, out var existing)
            || ShouldReplaceHost(existing, normalized))
        {
            index[address] = normalized;
        }
    }

    private static bool ShouldReplaceHost(string existing, string candidate)
    {
        var existingLabels = existing.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var candidateLabels = candidate.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return candidateLabels.Length < existingLabels.Length
            || (candidate.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
                && !existing.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase))
            || (candidate.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase)
                && !existing.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReversePointerToIpv4(string? pointer, out string address)
    {
        address = string.Empty;
        if (string.IsNullOrWhiteSpace(pointer)
            || !pointer.EndsWith(".in-addr.arpa", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var trimmed = pointer[..^".in-addr.arpa".Length];
        var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            return false;
        }

        Array.Reverse(parts);
        address = string.Join(".", parts);
        return IPAddress.TryParse(address, out _);
    }
}

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

internal static class ProcessCatalog
{
    public static ProcessRecord? Resolve(string query)
    {
        if (int.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
        {
            return Find(null).FirstOrDefault(process => process.ProcessId == pid) ?? TryReadByPid(pid);
        }

        return Find(query).FirstOrDefault();
    }

    public static IEnumerable<ProcessRecord> Find(string? query)
    {
        var processes = WmiProcess.ReadAll()
            .Select(process => process.ToRecord())
            .OrderBy(process => Score(process, query))
            .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(process => process.ProcessId)
            .AsEnumerable();

        if (!string.IsNullOrWhiteSpace(query))
        {
            processes = processes.Where(process =>
                process.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || process.ExecutablePath?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
                || process.CommandLine?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
                || process.ProcessId.ToString(CultureInfo.InvariantCulture).Equals(query, StringComparison.OrdinalIgnoreCase));
        }

        return processes;
    }

    private static int Score(ProcessRecord process, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 10;
        }

        if (process.ProcessId.ToString(CultureInfo.InvariantCulture).Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (process.Name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (Path.GetFileNameWithoutExtension(process.Name).Equals(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (process.ExecutablePath?.EndsWith(query + ".exe", StringComparison.OrdinalIgnoreCase) == true) return 3;
        if (process.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 4;
        return 5;
    }

    private static ProcessRecord? TryReadByPid(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new ProcessRecord(
                process.Id,
                0,
                process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process.ProcessName : process.ProcessName + ".exe",
                TryGet(() => process.MainModule?.FileName),
                null,
                TryGet(() => new DateTimeOffset(process.StartTime)),
                DateTimeOffset.Now,
                DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static T? TryGet<T>(Func<T?> get)
    {
        try
        {
            return get();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return default;
        }
    }
}

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
    private static readonly string CommonApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private static readonly string Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    private static readonly string Downloads = Path.Combine(UserProfile, "Downloads");

    public static string Classify(string path)
    {
        if (IsSystemRuntimeNoise(path)) return "system-runtime";
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
        if (IsSystemRuntimeNoise(path))
        {
            return false;
        }

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

    public static bool IsSystemRuntimeNoise(string path)
    {
        var normalized = path.Replace('/', '\\');
        return normalized.Contains("\\ProgramData\\Microsoft\\NetFramework\\BreadcrumbStore\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\Microsoft\\CLR_v4.0\\UsageLogs\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\assembly\\NativeImages_", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\AppData\\Local\\Microsoft\\CLR_v4.0\\", StringComparison.OrdinalIgnoreCase)
            || (IsUnder(normalized, CommonApplicationData) && normalized.Contains("\\Microsoft\\NetFramework\\", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsUsuallyNoise(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUnderAnyWatchRoot(string path, IReadOnlyList<string> roots)
    {
        if (path.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var root in roots)
        {
            if (IsUnder(path, root))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd('\\') + "\\";
            var fullRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

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
    SessionActivityOverview? ActivityOverview = null)
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
        IReadOnlyList<RegistryEvent> registryEvents)
    {
        var normalizedFileEvents = FileEventMerger.NormalizeForSession(fileEvents);
        var normalizedNetworkEvents = NetworkResolver.Enrich(networkEvents);
        var topFolders = BuildFolderImpact(normalizedFileEvents);
        var findings = Analyzer.Find(normalizedFileEvents, processes, normalizedNetworkEvents, registryEvents, topFolders);
        var aiActivity = AiCodingAnalyzer.Build(watchRoots, normalizedFileEvents, processes);
        var activityOverview = SessionActivityAnalyzer.Build(watchRoots, watchAll, normalizedFileEvents, normalizedNetworkEvents, aiActivity, findings);
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
            activityOverview);
    }

    public static SessionReport RefreshDerivedData(SessionReport session)
    {
        var normalizedFileEvents = FileEventMerger.NormalizeForSession(session.FileEvents);
        var normalizedNetworkEvents = NetworkResolver.Enrich(session.NetworkEvents);
        var topFolders = BuildFolderImpact(normalizedFileEvents);
        var findings = Analyzer.Find(normalizedFileEvents, session.Processes, normalizedNetworkEvents, session.RegistryEvents, topFolders);
        var aiActivity = AiCodingAnalyzer.Build(session.WatchRoots, normalizedFileEvents, session.Processes);
        var activityOverview = SessionActivityAnalyzer.Build(session.WatchRoots, session.WatchAll, normalizedFileEvents, normalizedNetworkEvents, aiActivity, findings);
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
            ActivityOverview = activityOverview
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

internal sealed record SessionActivityOverview(
    string Headline,
    IReadOnlyList<string> Highlights,
    IReadOnlyList<ActivityBucketSummary> Buckets);

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

        foreach (var group in files
            .Where(f => f.IsSensitive && !PathClassifier.IsSystemRuntimeNoise(f.Path))
            .GroupBy(f => NormalizePath(f.Path), StringComparer.OrdinalIgnoreCase)
            .Take(20))
        {
            var first = group.OrderBy(file => file.ObservedAt).First();
            var actions = string.Join(", ", group
                .Select(file => file.Kind.ToString().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(action => action, StringComparer.OrdinalIgnoreCase));
            findings.Add(new Finding("medium", "Sensitive path touched", $"{actions} {first.Path}"));
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

internal static class AiCodingAnalyzer
{
    private static readonly string[] ProjectNoiseSegments =
    [
        "\\node_modules\\",
        "\\.git\\",
        "\\bin\\",
        "\\obj\\",
        "\\dist\\",
        "\\build\\",
        "\\.next\\",
        "\\.turbo\\",
        "\\coverage\\"
    ];

    public static AiCodingActivity Build(
        IReadOnlyList<string> watchRoots,
        IReadOnlyList<FileEvent> files,
        IReadOnlyList<ProcessRecord> processes)
    {
        var changedProjectFiles = files
            .Where(file => file.Kind is FileEventKind.Created or FileEventKind.Modified or FileEventKind.Deleted or FileEventKind.Renamed)
            .Where(file => IsProjectFile(file.Path, watchRoots) || (!string.IsNullOrWhiteSpace(file.RelatedPath) && IsProjectFile(file.RelatedPath, watchRoots)))
            .GroupBy(file => $"{file.Kind}|{Normalize(file.Path)}|{NormalizeMaybe(file.RelatedPath)}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(file => SourceRank(file.Source)).ThenByDescending(file => file.ObservedAt).First())
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(file => new ProjectFileChange(
                file.Kind,
                file.Path,
                RelativeToWatchRoot(file.Path, watchRoots),
                file.RelatedPath,
                file.RelatedPath is null ? null : RelativeToWatchRoot(file.RelatedPath, watchRoots),
                file.Category,
                file.Source,
                file.ObservedAt))
            .ToList();

        var projectSummary = new ProjectChangeSummary(
            changedProjectFiles.Count(file => file.Kind == FileEventKind.Created),
            changedProjectFiles.Count(file => file.Kind == FileEventKind.Modified),
            changedProjectFiles.Count(file => file.Kind == FileEventKind.Deleted),
            changedProjectFiles.Count(file => file.Kind == FileEventKind.Renamed),
            changedProjectFiles.Select(file => Normalize(file.RelatedPath ?? file.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var rawDeveloperCommands = processes
            .Where(process => !string.IsNullOrWhiteSpace(process.CommandLine))
            .SelectMany(ClassifyCommands)
            .OrderBy(command => command.FirstSeen)
            .ThenBy(command => command.Kind, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var developerCommands = rawDeveloperCommands
            .Where(command => !IsLowSignalCommand(command))
            .GroupBy(command => $"{command.Kind}|{CommandFingerprint(command.CommandLine)}", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.OrderBy(command => command.FirstSeen).First();
                return first with { Occurrences = group.Count() };
            })
            .OrderBy(command => command.FirstSeen)
            .ThenBy(command => command.Kind, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var commandSummary = new CommandSummary(
            developerCommands.Count,
            developerCommands.Count(command => command.Kind == "package-install"),
            developerCommands.Count(command => command.Kind == "git"),
            developerCommands.Count(command => command.Kind == "test"),
            developerCommands.Count(command => command.Kind == "shell"),
            developerCommands.Count(command => command.Kind == "script"));

        var sensitiveAccesses = files
            .Where(file => file.IsSensitive)
            .GroupBy(file => $"{file.Kind}|{Normalize(file.Path)}|{file.ProcessId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(file => file.ObservedAt).First())
            .OrderBy(file => file.ObservedAt)
            .Select(file => new SensitiveAccess(
                file.Kind,
                file.Path,
                RelativeToWatchRoot(file.Path, watchRoots),
                file.Source,
                file.ProcessId,
                file.ProcessName,
                file.ObservedAt))
            .Take(50)
            .ToList();

        var processGroups = processes
            .GroupBy(process => NormalizeProcessName(process.Name), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProcessGroupSummary(
                group.Key,
                group.Count(),
                group.Count(process => !string.IsNullOrWhiteSpace(process.CommandLine)),
                group.Min(process => process.FirstSeen),
                group.Max(process => process.LastSeen)))
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();

        var processTimeline = processes
            .Where(IsTimelineProcess)
            .OrderBy(process => process.FirstSeen)
            .ThenBy(process => process.ProcessId)
            .Select(process => new ProcessTimelineItem(
                process.ProcessId,
                process.ParentProcessId,
                process.Name,
                SanitizeCommandLine(process.CommandLine),
                process.FirstSeen,
                process.LastSeen,
                Math.Max(0, (process.LastSeen - process.FirstSeen).TotalSeconds)))
            .Take(100)
            .ToList();

        return new AiCodingActivity(
            projectSummary,
            changedProjectFiles.Take(200).ToList(),
            commandSummary,
            developerCommands.Take(100).ToList(),
            sensitiveAccesses,
            processGroups,
            processTimeline);
    }

    private static bool IsProjectFile(string path, IReadOnlyList<string> watchRoots)
    {
        if (!PathClassifier.IsUnderAnyWatchRoot(path, watchRoots))
        {
            return false;
        }

        var normalized = path.Replace('/', '\\');
        if (ProjectNoiseSegments.Any(segment => normalized.Contains(segment, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var fileName = Path.GetFileName(normalized);
        return !fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".cache", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<CommandActivity> ClassifyCommands(ProcessRecord process)
    {
        var command = process.CommandLine ?? "";
        var name = process.Name;
        var kinds = new List<string>();

        if (IsPackageInstall(command)) kinds.Add("package-install");
        if (IsGitCommand(command, name)) kinds.Add("git");
        if (IsTestCommand(command)) kinds.Add("test");
        if (IsScriptCommand(command)) kinds.Add("script");
        if (IsShell(name, command)) kinds.Add("shell");

        foreach (var kind in kinds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return new CommandActivity(
                kind,
                process.ProcessId,
                process.ParentProcessId,
                process.Name,
                SanitizeCommandLine(process.CommandLine) ?? process.Name,
                process.FirstSeen,
                1);
        }
    }

    private static bool IsPackageInstall(string command) =>
        command.Contains("npm install", StringComparison.OrdinalIgnoreCase)
        || command.Contains("npm i ", StringComparison.OrdinalIgnoreCase)
        || command.EndsWith("npm i", StringComparison.OrdinalIgnoreCase)
        || command.Contains("pnpm add", StringComparison.OrdinalIgnoreCase)
        || command.Contains("pnpm install", StringComparison.OrdinalIgnoreCase)
        || command.Contains("yarn add", StringComparison.OrdinalIgnoreCase)
        || command.Contains("yarn install", StringComparison.OrdinalIgnoreCase)
        || (command.Contains("npm-cli.js", StringComparison.OrdinalIgnoreCase) && (command.Contains(" install", StringComparison.OrdinalIgnoreCase) || command.Contains(" add", StringComparison.OrdinalIgnoreCase)))
        || command.Contains("pip install", StringComparison.OrdinalIgnoreCase)
        || command.Contains("uv add", StringComparison.OrdinalIgnoreCase)
        || command.Contains("cargo add", StringComparison.OrdinalIgnoreCase)
        || command.Contains("dotnet add package", StringComparison.OrdinalIgnoreCase);

    private static bool IsTestCommand(string command) =>
        command.Contains("npm test", StringComparison.OrdinalIgnoreCase)
        || command.Contains("npm run test", StringComparison.OrdinalIgnoreCase)
        || command.Contains("pnpm test", StringComparison.OrdinalIgnoreCase)
        || command.Contains("yarn test", StringComparison.OrdinalIgnoreCase)
        || command.Contains("dotnet test", StringComparison.OrdinalIgnoreCase)
        || (command.Contains("npm-cli.js", StringComparison.OrdinalIgnoreCase) && command.Contains(" test", StringComparison.OrdinalIgnoreCase))
        || command.Contains("pytest", StringComparison.OrdinalIgnoreCase)
        || command.Contains("cargo test", StringComparison.OrdinalIgnoreCase)
        || command.Contains("go test", StringComparison.OrdinalIgnoreCase)
        || command.Contains("mvn test", StringComparison.OrdinalIgnoreCase)
        || command.Contains("gradle test", StringComparison.OrdinalIgnoreCase);

    private static bool IsScriptCommand(string command) =>
        command.Contains(".py", StringComparison.OrdinalIgnoreCase)
        || command.Contains(".ps1", StringComparison.OrdinalIgnoreCase)
        || command.Contains(".sh", StringComparison.OrdinalIgnoreCase)
        || command.Contains("node ", StringComparison.OrdinalIgnoreCase)
        || command.Contains("python ", StringComparison.OrdinalIgnoreCase)
        || command.Contains("pwsh ", StringComparison.OrdinalIgnoreCase);

    private static bool IsShell(string name, string command) =>
        name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
        || name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)
        || name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
        || command.Contains("ExecutionPolicy", StringComparison.OrdinalIgnoreCase);

    private static bool IsGitCommand(string command, string processName)
    {
        if (command.Contains("credential-manager", StringComparison.OrdinalIgnoreCase)
            || command.Contains("remote-https", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("credential-manager", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("git-remote-https.exe", StringComparison.OrdinalIgnoreCase)
            || command.Contains("git-remote-https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return processName.Equals("git.exe", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("git ", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("\"git\" ", StringComparison.OrdinalIgnoreCase)
            || command.Contains("\\git.exe\"", StringComparison.OrdinalIgnoreCase)
            || command.Contains("\\git.exe ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLowSignalCommand(CommandActivity command)
    {
        var name = NormalizeProcessName(command.ProcessName);
        var line = command.CommandLine;

        if (name is "conhost" or "git-credential-manager" or "git-remote-https")
        {
            return true;
        }

        return line.Contains("git credential-manager", StringComparison.OrdinalIgnoreCase)
            || line.Contains("git-remote-https", StringComparison.OrdinalIgnoreCase)
            || line.Contains("git remote-https", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Win32_Process | Select-Object", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Long-lived PowerShell AST parser", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTimelineProcess(ProcessRecord process)
    {
        var name = NormalizeProcessName(process.Name);
        if (name is "conhost" or "git-credential-manager" or "git-remote-https")
        {
            return false;
        }

        var command = process.CommandLine ?? "";
        if (command.Contains("credential-manager", StringComparison.OrdinalIgnoreCase)
            || command.Contains("git-remote-https", StringComparison.OrdinalIgnoreCase)
            || command.Contains("git remote-https", StringComparison.OrdinalIgnoreCase)
            || command.Contains("Win32_Process | Select-Object", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return process.ParentProcessId == 0
            || !string.IsNullOrWhiteSpace(process.CommandLine)
            || name is "codex" or "code" or "cursor" or "node" or "node_repl" or "git" or "powershell" or "pwsh" or "cmd" or "dotnet" or "python" or "npm" or "pnpm";
    }

    private static string NormalizeProcessName(string name)
    {
        var file = Path.GetFileNameWithoutExtension(name);
        return string.IsNullOrWhiteSpace(file) ? name : file;
    }

    private static string CommandFingerprint(string command)
    {
        var normalized = command
            .Replace("\"", "", StringComparison.Ordinal)
            .Replace("C:\\Program Files\\Git\\cmd\\git.exe", "git", StringComparison.OrdinalIgnoreCase)
            .Replace("C:\\Program Files\\Git\\mingw64\\bin\\git.exe", "git", StringComparison.OrdinalIgnoreCase)
            .Replace("C:/Program Files/Git/mingw64/libexec/git-core\\git.exe", "git", StringComparison.OrdinalIgnoreCase)
            .Replace("C:\\windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe", "powershell", StringComparison.OrdinalIgnoreCase)
            .Replace("C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe", "powershell", StringComparison.OrdinalIgnoreCase);

        return string.Join(" ", normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string? SanitizeCommandLine(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return command;
        }

        var compact = command
            .Replace("\"C:\\Program Files\\Git\\cmd\\git.exe\"", "git", StringComparison.OrdinalIgnoreCase)
            .Replace("\"C:\\Program Files\\Git\\mingw64\\bin\\git.exe\"", "git", StringComparison.OrdinalIgnoreCase)
            .Replace("\"C:/Program Files/Git/mingw64/libexec/git-core\\git.exe\"", "git", StringComparison.OrdinalIgnoreCase)
            .Replace("\"C:\\windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\"", "powershell", StringComparison.OrdinalIgnoreCase)
            .Replace("\"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\"", "powershell", StringComparison.OrdinalIgnoreCase)
            .Replace("git.exe ", "git ", StringComparison.OrdinalIgnoreCase);

        var encodedIndex = compact.IndexOf("-EncodedCommand", StringComparison.OrdinalIgnoreCase);
        if (encodedIndex >= 0)
        {
            return compact[..encodedIndex].Trim() + " -EncodedCommand <base64>";
        }

        return compact.Length <= 320 ? compact : compact[..317] + "...";
    }

    private static int SourceRank(string source) => source.Equals("etw", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\');

    private static string NormalizeMaybe(string? path) => string.IsNullOrWhiteSpace(path) ? "" : Normalize(path);

    private static string RelativeToWatchRoot(string path, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            try
            {
                var relative = Path.GetRelativePath(root, path);
                if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                {
                    return relative;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                continue;
            }
        }

        return path;
    }
}

internal static class SessionActivityAnalyzer
{
    public static SessionActivityOverview Build(
        IReadOnlyList<string> watchRoots,
        bool watchAll,
        IReadOnlyList<FileEvent> files,
        IReadOnlyList<NetworkEvent> networkEvents,
        AiCodingActivity ai,
        IReadOnlyList<Finding> findings)
    {
        var writes = files
            .Where(file => file.Kind is FileEventKind.Created or FileEventKind.Modified or FileEventKind.Deleted or FileEventKind.Renamed)
            .ToList();

        var buckets = new List<ActivityBucketSummary>();

        var appDataBucket = BuildFileBucket(
            "app-data-cache",
            "App Data / Cache",
            "Writes under roaming/local app data.",
            writes.Where(file => file.Category == "app-data"));
        AddBucket(buckets, appDataBucket);

        var tempBucket = BuildFileBucket(
            "temp-churn",
            "Temp Churn",
            "Writes under temp folders.",
            writes.Where(file => file.Category == "temp"));
        AddBucket(buckets, tempBucket);

        var workspaceBucket = BuildFileBucket(
            "workspace",
            "Project / User Files",
            watchRoots.Count > 0
                ? "Writes under watched roots and user-facing folders."
                : "Writes under documents, desktop, and downloads.",
            writes.Where(file => IsWorkspaceFile(file, watchRoots)));
        AddBucket(buckets, workspaceBucket);

        var gitBucket = BuildFileBucket(
            "git-metadata",
            "Git Metadata",
            "Internal .git activity summarized separately from project files.",
            writes.Where(file => IsGitPath(file.Path) || (!string.IsNullOrWhiteSpace(file.RelatedPath) && IsGitPath(file.RelatedPath))));
        AddBucket(buckets, gitBucket);

        var runtimeBucket = BuildFileBucket(
            "system-runtime",
            "System / Runtime",
            "Framework and runtime bookkeeping activity.",
            writes.Where(file => PathClassifier.IsSystemRuntimeNoise(file.Path) || (!string.IsNullOrWhiteSpace(file.RelatedPath) && PathClassifier.IsSystemRuntimeNoise(file.RelatedPath))));
        AddBucket(buckets, runtimeBucket);

        var sensitiveBucket = BuildSensitiveBucket(files);
        AddBucket(buckets, sensitiveBucket);

        var networkBucket = BuildNetworkBucket(networkEvents);
        AddBucket(buckets, networkBucket);

        buckets = buckets
            .OrderByDescending(bucket => BucketRank(bucket.Key))
            .ThenByDescending(bucket => bucket.EventCount)
            .ThenBy(bucket => bucket.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var highlights = BuildHighlights(watchAll, ai, findings, buckets);
        var headline = BuildHeadline(ai, findings, buckets, networkEvents.Count);

        return new SessionActivityOverview(headline, highlights, buckets);
    }

    private static ActivityBucketSummary? BuildFileBucket(string key, string label, string description, IEnumerable<FileEvent> source)
    {
        var entries = source.ToList();
        if (entries.Count == 0)
        {
            return null;
        }

        var examples = entries
            .Select(file => DescribePathForSummary(EffectivePath(file)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

        return new ActivityBucketSummary(
            key,
            label,
            description,
            entries.Count,
            entries.Select(file => NormalizeSummaryPath(EffectivePath(file))).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            entries.Where(file => file.Kind != FileEventKind.Deleted).Sum(file => Math.Max(0, file.SizeDelta)),
            examples);
    }

    private static ActivityBucketSummary? BuildSensitiveBucket(IReadOnlyList<FileEvent> files)
    {
        var entries = files
            .Where(file => file.IsSensitive)
            .ToList();

        if (entries.Count == 0)
        {
            return null;
        }

        var examples = entries
            .Select(file => DescribePathForSummary(file.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

        return new ActivityBucketSummary(
            "sensitive-paths",
            "Sensitive Paths",
            "Reads or writes touching sensitive config or secret-like paths.",
            entries.Count,
            entries.Select(file => NormalizeSummaryPath(file.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            0,
            examples);
    }

    private static ActivityBucketSummary? BuildNetworkBucket(IReadOnlyList<NetworkEvent> networkEvents)
    {
        if (networkEvents.Count == 0)
        {
            return null;
        }

        var examples = networkEvents
            .Select(NetworkResolver.DisplayHost)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

        return new ActivityBucketSummary(
            "network",
            "Network Destinations",
            "Distinct remote endpoints contacted by the session process tree.",
            networkEvents.Count,
            networkEvents.Select(item => $"{NetworkResolver.DisplayHost(item)}:{item.RemotePort}").Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            0,
            examples);
    }

    private static List<string> BuildHighlights(bool watchAll, AiCodingActivity ai, IReadOnlyList<Finding> findings, IReadOnlyList<ActivityBucketSummary> buckets)
    {
        var highlights = new List<string>();

        if (watchAll)
        {
            var topBucket = buckets
                .Where(bucket => bucket.Key is not "network" and not "sensitive-paths")
                .OrderByDescending(bucket => bucket.EventCount)
                .ThenByDescending(bucket => bucket.UniquePathCount)
                .FirstOrDefault();

            if (topBucket is not null)
            {
                highlights.Add($"Most activity was {topBucket.Label.ToLowerInvariant()} ({topBucket.EventCount:N0} events).");
            }
        }

        if (ai.ProjectChanges.TotalChanged > 0)
        {
            highlights.Add($"Changed {ai.ProjectChanges.TotalChanged:N0} project files.");
        }

        if (ai.Commands.GitCommands > 0)
        {
            highlights.Add($"Ran {ai.Commands.GitCommands:N0} git commands.");
        }

        if (ai.Commands.PackageInstalls > 0)
        {
            highlights.Add($"Installed packages {ai.Commands.PackageInstalls:N0} time(s).");
        }

        if (ai.SensitiveAccesses.Count > 0)
        {
            highlights.Add($"Touched {ai.SensitiveAccesses.Count:N0} sensitive path event(s).");
        }

        if (findings.Count(f => f.Severity is "high" or "medium") > 0)
        {
            highlights.Add($"Raised {findings.Count(f => f.Severity is "high" or "medium"):N0} medium/high finding(s).");
        }

        if (buckets.Any(bucket => bucket.Key == "network"))
        {
            var network = buckets.First(bucket => bucket.Key == "network");
            highlights.Add($"Contacted {network.UniquePathCount:N0} network endpoint(s).");
        }

        return highlights.Take(5).ToList();
    }

    private static string BuildHeadline(AiCodingActivity ai, IReadOnlyList<Finding> findings, IReadOnlyList<ActivityBucketSummary> buckets, int networkCount)
    {
        var topBucket = buckets
            .Where(bucket => bucket.Key is not "network" and not "sensitive-paths")
            .OrderByDescending(bucket => bucket.EventCount)
            .ThenByDescending(bucket => bucket.UniquePathCount)
            .FirstOrDefault();

        var parts = new List<string>();

        if (topBucket is not null)
        {
            parts.Add($"Mostly {topBucket.Label.ToLowerInvariant()}");
        }

        if (ai.ProjectChanges.TotalChanged > 0)
        {
            parts.Add($"changed {ai.ProjectChanges.TotalChanged:N0} project files");
        }

        if (ai.Commands.GitCommands > 0)
        {
            parts.Add($"ran {ai.Commands.GitCommands:N0} git commands");
        }
        else if (ai.Commands.Total > 0)
        {
            parts.Add($"ran {ai.Commands.Total:N0} developer commands");
        }

        if (ai.SensitiveAccesses.Count > 0 || findings.Any(f => f.Title.Contains("Sensitive path", StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add("touched sensitive paths");
        }

        if (networkCount > 0)
        {
            parts.Add($"contacted {networkCount:N0} network endpoint(s)");
        }

        if (parts.Count == 0)
        {
            return "No high-signal activity summary was derived from this session.";
        }

        return string.Join(", ", parts) + ".";
    }

    private static bool IsWorkspaceFile(FileEvent file, IReadOnlyList<string> watchRoots)
    {
        var effectivePath = EffectivePath(file);
        if (string.IsNullOrWhiteSpace(effectivePath))
        {
            return false;
        }

        if (IsGitPath(effectivePath) || PathClassifier.IsSystemRuntimeNoise(effectivePath))
        {
            return false;
        }

        if (watchRoots.Count > 0 && PathClassifier.IsUnderAnyWatchRoot(effectivePath, watchRoots))
        {
            return true;
        }

        var category = PathClassifier.Classify(effectivePath);
        return category is "documents" or "desktop" or "downloads";
    }

    private static string EffectivePath(FileEvent file) =>
        file.Kind == FileEventKind.Renamed && !string.IsNullOrWhiteSpace(file.RelatedPath)
            ? file.RelatedPath!
            : file.Path;

    private static void AddBucket(List<ActivityBucketSummary> buckets, ActivityBucketSummary? bucket)
    {
        if (bucket is not null)
        {
            buckets.Add(bucket);
        }
    }

    private static int BucketRank(string key) => key switch
    {
        "workspace" => 100,
        "sensitive-paths" => 95,
        "app-data-cache" => 90,
        "temp-churn" => 80,
        "git-metadata" => 70,
        "system-runtime" => 60,
        "network" => 50,
        _ => 0
    };

    private static bool IsGitPath(string path) =>
        path.Replace('/', '\\').Contains("\\.git\\", StringComparison.OrdinalIgnoreCase);

    private static string DescribePathForSummary(string path)
    {
        var normalized = path.Replace('/', '\\');
        if (normalized.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase))
        {
            return HtmlReport.DescribeGitInternalPath(path);
        }

        if (PathClassifier.IsSystemRuntimeNoise(path))
        {
            return HtmlReport.DescribeSystemRuntimePath(path);
        }

        return normalized;
    }

    private static string NormalizeSummaryPath(string path)
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

internal static class SessionOutputs
{
    public static async Task<IReadOnlyList<string>> WriteAsync(SessionReport session, string outputDirectory, JsonSerializerOptions jsonOptions)
    {
        Directory.CreateDirectory(outputDirectory);
        session = SessionReport.RefreshDerivedData(session);

        var jsonPath = Path.Combine(outputDirectory, "session.json");
        var htmlPath = Path.Combine(outputDirectory, "report.html");
        var csvPath = Path.Combine(outputDirectory, "touched-files.csv");
        var commandsPath = Path.Combine(outputDirectory, "commands.json");
        var aiActivityPath = Path.Combine(outputDirectory, "ai-activity.json");
        var cleanupPath = Path.Combine(outputDirectory, "cleanup.ps1");

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(session, jsonOptions), Encoding.UTF8);
        await File.WriteAllTextAsync(htmlPath, HtmlReport.Render(session), Encoding.UTF8);
        await File.WriteAllTextAsync(csvPath, CsvReport.RenderFiles(session.FileEvents), Encoding.UTF8);
        await File.WriteAllTextAsync(commandsPath, JsonSerializer.Serialize(session.Processes.Where(p => !string.IsNullOrWhiteSpace(p.CommandLine)), jsonOptions), Encoding.UTF8);
        await File.WriteAllTextAsync(aiActivityPath, JsonSerializer.Serialize(session.AiActivity, jsonOptions), Encoding.UTF8);
        await File.WriteAllTextAsync(cleanupPath, CleanupScript.Render(session), Encoding.UTF8);

        return [htmlPath, jsonPath, csvPath, commandsPath, aiActivityPath, cleanupPath];
    }
}

internal static class SessionStore
{
    public static void Write(string path, SessionReport session)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var transaction = connection.BeginTransaction();

        Execute(connection, transaction, """
            CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE processes (process_id INTEGER, parent_process_id INTEGER, name TEXT, executable_path TEXT, command_line TEXT, first_seen TEXT, last_seen TEXT);
            CREATE TABLE file_events (kind TEXT, path TEXT, category TEXT, source TEXT, observed_at TEXT, process_id INTEGER, process_name TEXT, size_delta INTEGER, is_sensitive INTEGER, related_path TEXT);
            CREATE TABLE network_events (process_id INTEGER, local_address TEXT, local_port INTEGER, remote_address TEXT, remote_port INTEGER, state TEXT, first_seen TEXT, remote_host TEXT);
            CREATE TABLE registry_events (kind TEXT, key TEXT, before_value TEXT, after_value TEXT);
            CREATE TABLE findings (severity TEXT, title TEXT, detail TEXT);
            """);

        Insert(connection, transaction, "INSERT INTO metadata(key, value) VALUES ($key, $value)", new()
        {
            ["$key"] = "session_json",
            ["$value"] = JsonSerializer.Serialize(session, ProgramJson.Options)
        });

        foreach (var process in session.Processes)
        {
            Insert(connection, transaction, "INSERT INTO processes VALUES ($pid, $parent, $name, $exe, $cmd, $first, $last)", new()
            {
                ["$pid"] = process.ProcessId,
                ["$parent"] = process.ParentProcessId,
                ["$name"] = process.Name,
                ["$exe"] = process.ExecutablePath,
                ["$cmd"] = process.CommandLine,
                ["$first"] = process.FirstSeen.ToString("O", CultureInfo.InvariantCulture),
                ["$last"] = process.LastSeen.ToString("O", CultureInfo.InvariantCulture)
            });
        }

        foreach (var file in session.FileEvents)
        {
            Insert(connection, transaction, "INSERT INTO file_events VALUES ($kind, $path, $category, $source, $observed, $pid, $pname, $delta, $sensitive, $related)", new()
            {
                ["$kind"] = file.Kind.ToString(),
                ["$path"] = file.Path,
                ["$category"] = file.Category,
                ["$source"] = file.Source,
                ["$observed"] = file.ObservedAt.ToString("O", CultureInfo.InvariantCulture),
                ["$pid"] = file.ProcessId,
                ["$pname"] = file.ProcessName,
                ["$delta"] = file.SizeDelta,
                ["$sensitive"] = file.IsSensitive ? 1 : 0,
                ["$related"] = file.RelatedPath
            });
        }

        foreach (var item in session.NetworkEvents)
        {
            Insert(connection, transaction, "INSERT INTO network_events VALUES ($pid, $local, $lport, $remote, $rport, $state, $first, $host)", new()
            {
                ["$pid"] = item.ProcessId,
                ["$local"] = item.LocalAddress,
                ["$lport"] = item.LocalPort,
                ["$remote"] = item.RemoteAddress,
                ["$rport"] = item.RemotePort,
                ["$state"] = item.State,
                ["$first"] = item.FirstSeen.ToString("O", CultureInfo.InvariantCulture),
                ["$host"] = item.RemoteHost
            });
        }

        foreach (var item in session.RegistryEvents)
        {
            Insert(connection, transaction, "INSERT INTO registry_events VALUES ($kind, $key, $before, $after)", new()
            {
                ["$kind"] = item.Kind.ToString(),
                ["$key"] = item.Key,
                ["$before"] = item.Before,
                ["$after"] = item.After
            });
        }

        foreach (var finding in session.Findings)
        {
            Insert(connection, transaction, "INSERT INTO findings VALUES ($severity, $title, $detail)", new()
            {
                ["$severity"] = finding.Severity,
                ["$title"] = finding.Title,
                ["$detail"] = finding.Detail
            });
        }

        transaction.Commit();
    }

    public static SessionReport? Read(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = 'session_json'";
        var value = command.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(value)
            ? null
            : JsonSerializer.Deserialize<SessionReport>(value, ProgramJson.Options);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, string sql, Dictionary<string, object?> values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (key, value) in values)
        {
            command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }
}

internal static class ProgramJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}

internal static class HtmlReport
{
    public static string Render(SessionReport session)
    {
        var ai = AiCodingAnalyzer.Build(session.WatchRoots, session.FileEvents, session.Processes);
        var appName = WebUtility.HtmlEncode(Path.GetFileName(session.App));
        var activityOverview = session.ActivityOverview ?? SessionActivityAnalyzer.Build(session.WatchRoots, session.WatchAll, session.FileEvents, session.NetworkEvents, ai, session.Findings);
        var rows = string.Join(Environment.NewLine, VisibleFileEvents(session.FileEvents).Select(RenderFileRow));
        var processes = string.Join(Environment.NewLine, session.Processes.Select(p => $"<tr><td>{p.ProcessId}</td><td>{Esc(p.Name)}</td><td>{Esc(p.CommandLine ?? "")}</td></tr>"));
        var network = string.Join(Environment.NewLine, session.NetworkEvents.Take(200).Select(RenderNetworkRow));
        var findings = string.Join(Environment.NewLine, session.Findings.Select(f => $"<li class=\"{Esc(f.Severity)}\"><strong>{Esc(f.Severity.ToUpperInvariant())}</strong> {Esc(f.Title)}<br><span>{Esc(f.Detail)}</span></li>"));
        var folders = string.Join(Environment.NewLine, session.TopFolders.Where(f => !IsGitInternalPath(f.Path) && !PathClassifier.IsSystemRuntimeNoise(f.Path)).Select(f => $"<tr><td>{Esc(f.Path)}</td><td>{Esc(f.Category)}</td><td>{f.FileCount:N0}</td><td>{Format.Bytes(f.BytesAdded)}</td></tr>"));
        var projectFiles = string.Join(Environment.NewLine, ai.ChangedProjectFiles.Select(RenderProjectFileRow));
        var commands = string.Join(Environment.NewLine, ai.DeveloperCommands.Select(RenderCommandRow));
        var sensitive = string.Join(Environment.NewLine, ai.SensitiveAccesses.Select(RenderSensitiveRow));
        var processGroups = string.Join(Environment.NewLine, ai.ProcessGroups.Select(RenderProcessGroupRow));
        var timeline = string.Join(Environment.NewLine, ai.ProcessTimeline.Select(RenderTimelineRow));
        var gitMetadata = SummarizeGitInternalActivity(session.FileEvents);
        var gitMetadataExamples = string.Join(Environment.NewLine, gitMetadata.Examples.Select(example => $"<li><code>{Esc(example)}</code></li>"));
        var runtimeNoise = SummarizeSystemRuntimeActivity(session.FileEvents);
        var runtimeNoiseExamples = string.Join(Environment.NewLine, runtimeNoise.Examples.Select(example => $"<li><code>{Esc(example)}</code></li>"));
        var activityHighlights = string.Join(Environment.NewLine, activityOverview.Highlights.Select(highlight => $"<li>{Esc(highlight)}</li>"));
        var activityBuckets = string.Join(Environment.NewLine, activityOverview.Buckets.Select(RenderActivityBucket));

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
            .tag { display:inline-block; border:1px solid var(--line); border-radius:999px; padding:2px 8px; background:#f7f9fc; color:#364454; font-size:12px; }
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
            <p>{{Esc(session.StartedAt.ToString("g"))}} - {{Esc(session.EndedAt.ToString("g"))}} - {{Esc(RenderWatchScope(session))}}</p>
          </header>
          <main>
            <section class="grid">
              <div class="metric"><strong>{{session.Summary.FilesRead:N0}}</strong><span>file reads</span></div>
              <div class="metric"><strong>{{session.Summary.FilesCreated:N0}}</strong><span>files created</span></div>
              <div class="metric"><strong>{{session.Summary.FilesModified:N0}}</strong><span>files modified</span></div>
              <div class="metric"><strong>{{session.Summary.FilesDeleted:N0}}</strong><span>files deleted</span></div>
              <div class="metric"><strong>{{session.Summary.FilesRenamed:N0}}</strong><span>files renamed</span></div>
              <div class="metric"><strong>{{Format.Bytes(session.Summary.BytesAddedOrChanged)}}</strong><span>added or changed</span></div>
              <div class="metric"><strong>{{session.Summary.CommandCount:N0}}</strong><span>commands captured</span></div>
              <div class="metric"><strong>{{session.Summary.NetworkConnectionCount:N0}}</strong><span>network endpoints</span></div>
            </section>

            <section>
              <h2>Big Picture</h2>
              <div class="panel"><div style="padding:16px 18px;">
                <p><strong>{{Esc(activityOverview.Headline)}}</strong></p>
                {{(activityOverview.Highlights.Count == 0 ? "<p class=\"muted\">No extra highlights were derived for this session.</p>" : $"<ul>{activityHighlights}</ul>")}}
              </div></div>
            </section>

            <section>
              <h2>Activity Buckets</h2>
              {{(activityOverview.Buckets.Count == 0 ? "<p class=\"muted\">No bucket summary was derived for this session.</p>" : $"<div class=\"panel\"><table><thead><tr><th>Bucket</th><th>Events</th><th>Unique Paths</th><th>Bytes</th><th>Examples</th></tr></thead><tbody>{activityBuckets}</tbody></table></div>")}}
            </section>

            <section>
              <h2>Risky Observations</h2>
              {{(session.Findings.Count == 0 ? "<p class=\"muted\">No risky observations from the Phase 1 analyzers.</p>" : $"<ul class=\"findings\">{findings}</ul>")}}
            </section>

            <section>
              <h2>AI Coding Activity</h2>
              <div class="grid">
                <div class="metric"><strong>{{ai.ProjectChanges.TotalChanged:N0}}</strong><span>project files changed</span></div>
                <div class="metric"><strong>{{ai.Commands.PackageInstalls:N0}}</strong><span>package installs</span></div>
                <div class="metric"><strong>{{ai.Commands.GitCommands:N0}}</strong><span>git commands</span></div>
                <div class="metric"><strong>{{ai.Commands.TestCommands:N0}}</strong><span>test commands</span></div>
                <div class="metric"><strong>{{ai.SensitiveAccesses.Count:N0}}</strong><span>sensitive accesses</span></div>
                <div class="metric"><strong>{{ai.ProcessGroups.Count:N0}}</strong><span>process groups</span></div>
              </div>
            </section>

            <section>
              <h2>Changed Project Files</h2>
              {{(ai.ChangedProjectFiles.Count == 0 ? "<p class=\"muted\">No project file changes detected under the watched roots.</p>" : $"<div class=\"panel\"><table><thead><tr><th>Action</th><th>Source</th><th>Category</th><th>Path</th></tr></thead><tbody>{projectFiles}</tbody></table></div>")}}
            </section>

            <section>
              <h2>Git Repository Metadata</h2>
              {{(gitMetadata.Total == 0
                  ? "<p class=\"muted\">No internal .git write activity was summarized for this session.</p>"
                  : $"""
              <div class="grid">
                <div class="metric"><strong>{gitMetadata.Total:N0}</strong><span>.git internal writes</span></div>
                <div class="metric"><strong>{gitMetadata.Created:N0}</strong><span>created</span></div>
                <div class="metric"><strong>{gitMetadata.Modified:N0}</strong><span>modified</span></div>
                <div class="metric"><strong>{gitMetadata.Deleted:N0}</strong><span>deleted</span></div>
                <div class="metric"><strong>{gitMetadata.Renamed:N0}</strong><span>renamed</span></div>
              </div>
              <p class="muted">Internal repository writes such as objects, refs, and logs are summarized here instead of being mixed into the main file tables. Raw events remain in JSON, CSV, and SQLite exports.</p>
              <div class="panel"><div style="padding:14px 16px;"><ul>{gitMetadataExamples}</ul></div></div>
              """)}}
            </section>

            <section>
              <h2>System Runtime Activity</h2>
              {{(runtimeNoise.Total == 0
                  ? "<p class=\"muted\">No framework or runtime noise was summarized for this session.</p>"
                  : $"""
              <div class="grid">
                <div class="metric"><strong>{runtimeNoise.Total:N0}</strong><span>runtime writes</span></div>
                <div class="metric"><strong>{runtimeNoise.Created:N0}</strong><span>created</span></div>
                <div class="metric"><strong>{runtimeNoise.Modified:N0}</strong><span>modified</span></div>
                <div class="metric"><strong>{runtimeNoise.Deleted:N0}</strong><span>deleted</span></div>
                <div class="metric"><strong>{runtimeNoise.Renamed:N0}</strong><span>renamed</span></div>
              </div>
              <p class="muted">Framework and runtime bookkeeping is summarized here instead of being treated as sensitive or mixed into the main file tables. Raw events remain in JSON, CSV, and SQLite exports.</p>
              <div class="panel"><div style="padding:14px 16px;"><ul>{runtimeNoiseExamples}</ul></div></div>
              """)}}
            </section>

            <section>
              <h2>Developer Commands</h2>
              {{(ai.DeveloperCommands.Count == 0 ? "<p class=\"muted\">No package, git, test, shell, or script commands detected.</p>" : $"<div class=\"panel\"><table><thead><tr><th>Kind</th><th>Seen</th><th>First PID</th><th>Process</th><th>Command</th></tr></thead><tbody>{commands}</tbody></table></div>")}}
            </section>

            <section>
              <h2>Sensitive Access</h2>
              {{(ai.SensitiveAccesses.Count == 0 ? "<p class=\"muted\">No sensitive paths detected in the watched roots.</p>" : $"<div class=\"panel\"><table><thead><tr><th>Action</th><th>Source</th><th>PID</th><th>Process</th><th>Path</th></tr></thead><tbody>{sensitive}</tbody></table></div>")}}
            </section>

            <section>
              <h2>Process Summary</h2>
              <div class="panel"><table><thead><tr><th>Process</th><th>Seen</th><th>With command</th><th>First seen</th><th>Last seen</th></tr></thead><tbody>{{processGroups}}</tbody></table></div>
            </section>

            <section>
              <h2>Signal Process Timeline</h2>
              <div class="panel"><table><thead><tr><th>First Seen</th><th>PID</th><th>Parent</th><th>Name</th><th>Duration</th><th>Command</th></tr></thead><tbody>{{timeline}}</tbody></table></div>
            </section>

            <section>
              <h2>Top Folders Touched</h2>
              {{(string.IsNullOrWhiteSpace(folders) ? "<p class=\"muted\">No non-.git, non-runtime folder writes were summarized for this session.</p>" : $"<div class=\"panel\"><table><thead><tr><th>Folder</th><th>Category</th><th>Files</th><th>Growth</th></tr></thead><tbody>{folders}</tbody></table></div>")}}
            </section>

            <section>
              <h2>Files</h2>
              <p class="muted">This table prioritizes writes, sensitive reads, and a deduplicated sample of other reads. Internal .git and runtime bookkeeping writes are summarized separately. Raw events remain in JSON, CSV, and SQLite exports.</p>
              <div class="panel"><table><thead><tr><th>Action</th><th>Source</th><th>PID</th><th>Category</th><th>Delta</th><th>Path</th></tr></thead><tbody>{{rows}}</tbody></table></div>
            </section>

            <section>
              <h2>Child Processes and Commands</h2>
              <div class="panel"><table><thead><tr><th>PID</th><th>Name</th><th>Command</th></tr></thead><tbody>{{processes}}</tbody></table></div>
            </section>

            <section>
              <h2>Network</h2>
              <div class="panel"><table><thead><tr><th>PID</th><th>Remote Host</th><th>Remote</th><th>State</th></tr></thead><tbody>{{network}}</tbody></table></div>
            </section>

            <p class="muted">Phase 1 uses ETW file/process events when elevated, live process/network sampling, and before/after file snapshots as fallback. Packet contents are not collected.</p>
          </main>
        </body>
        </html>
        """;
    }

    private static string RenderFileRow(FileEvent file) =>
        $"<tr><td>{Esc(file.Kind.ToString())}</td><td>{Esc(file.Source)}</td><td>{Esc(file.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "")}</td><td>{Esc(file.Category)}</td><td>{Format.Bytes(file.SizeDelta)}</td><td><code>{Esc(RenderFilePath(file))}</code></td></tr>";

    private static string RenderProjectFileRow(ProjectFileChange file) =>
        $"<tr><td>{Esc(file.Kind.ToString())}</td><td>{Esc(file.Source)}</td><td>{Esc(file.Category)}</td><td><code>{Esc(RenderProjectPath(file))}</code></td></tr>";

    private static string RenderCommandRow(CommandActivity command) =>
        $"<tr><td><span class=\"tag\">{Esc(command.Kind)}</span></td><td>{command.Occurrences:N0}</td><td>{command.ProcessId}</td><td>{Esc(command.ProcessName)}</td><td><code>{Esc(command.CommandLine)}</code></td></tr>";

    private static string RenderSensitiveRow(SensitiveAccess access) =>
        $"<tr><td>{Esc(access.Kind.ToString())}</td><td>{Esc(access.Source)}</td><td>{Esc(access.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "")}</td><td>{Esc(access.ProcessName ?? "")}</td><td><code>{Esc(access.RelativePath)}</code></td></tr>";

    private static string RenderProcessGroupRow(ProcessGroupSummary group) =>
        $"<tr><td>{Esc(group.Name)}</td><td>{group.Count:N0}</td><td>{group.WithCommandLine:N0}</td><td>{Esc(group.FirstSeen.ToString("T", CultureInfo.InvariantCulture))}</td><td>{Esc(group.LastSeen.ToString("T", CultureInfo.InvariantCulture))}</td></tr>";

    private static string RenderTimelineRow(ProcessTimelineItem item) =>
        $"<tr><td>{Esc(item.FirstSeen.ToString("T", CultureInfo.InvariantCulture))}</td><td>{item.ProcessId}</td><td>{item.ParentProcessId}</td><td>{Esc(item.Name)}</td><td>{item.DurationSeconds:0.0}s</td><td><code>{Esc(item.CommandLine ?? "")}</code></td></tr>";

    private static string RenderNetworkRow(NetworkEvent item) =>
        $"<tr><td>{item.ProcessId}</td><td>{Esc(NetworkResolver.DisplayHost(item))}</td><td>{Esc(item.RemoteAddress)}:{item.RemotePort}</td><td>{Esc(item.State)}</td></tr>";

    private static string RenderActivityBucket(ActivityBucketSummary bucket)
    {
        var examples = bucket.Examples.Count == 0
            ? "<span class=\"muted\">None</span>"
            : string.Join("<br>", bucket.Examples.Select(example => $"<code>{Esc(example)}</code>"));

        return $"<tr><td><strong>{Esc(bucket.Label)}</strong><br><span class=\"muted\">{Esc(bucket.Description)}</span></td><td>{bucket.EventCount:N0}</td><td>{bucket.UniquePathCount:N0}</td><td>{Format.Bytes(bucket.BytesChanged)}</td><td>{examples}</td></tr>";
    }

    private static IEnumerable<FileEvent> VisibleFileEvents(IReadOnlyList<FileEvent> events)
    {
        var writes = events
            .Where(file => file.Kind is FileEventKind.Created or FileEventKind.Modified or FileEventKind.Deleted or FileEventKind.Renamed)
            .Where(file => !IsGitInternalPath(file.Path) && !PathClassifier.IsSystemRuntimeNoise(file.Path))
            .OrderBy(file => file.ObservedAt)
            .Take(120);

        var sensitiveReads = events
            .Where(file => file.Kind == FileEventKind.Read && file.IsSensitive)
            .GroupBy(file => NormalizeVisiblePath(file.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(file => file.ObservedAt).First())
            .Take(40);

        var readSample = events
            .Where(file => file.Kind == FileEventKind.Read && !file.IsSensitive && !IsBoringRead(file.Path))
            .GroupBy(file => $"{NormalizeVisiblePath(file.Path)}|{file.ProcessName}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(file => file.ObservedAt).First())
            .Take(80);

        return writes.Concat(sensitiveReads).Concat(readSample).Take(200);
    }

    private static bool IsBoringRead(string path)
    {
        var normalized = path.Replace('/', '\\');
        return normalized.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\node_modules\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase);
    }

    private static GitInternalSummary SummarizeGitInternalActivity(IReadOnlyList<FileEvent> events)
    {
        var writes = events
            .Where(file => file.Kind is FileEventKind.Created or FileEventKind.Modified or FileEventKind.Deleted or FileEventKind.Renamed)
            .Where(file => IsGitInternalPath(file.Path))
            .ToList();

        var examples = writes
            .Select(file => DescribeGitInternalPath(file.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        return new GitInternalSummary(
            writes.Count,
            writes.Count(file => file.Kind == FileEventKind.Created),
            writes.Count(file => file.Kind == FileEventKind.Modified),
            writes.Count(file => file.Kind == FileEventKind.Deleted),
            writes.Count(file => file.Kind == FileEventKind.Renamed),
            examples);
    }

    private static SystemRuntimeSummary SummarizeSystemRuntimeActivity(IReadOnlyList<FileEvent> events)
    {
        var writes = events
            .Where(file => file.Kind is FileEventKind.Created or FileEventKind.Modified or FileEventKind.Deleted or FileEventKind.Renamed)
            .Where(file => PathClassifier.IsSystemRuntimeNoise(file.Path))
            .ToList();

        var examples = writes
            .Select(file => DescribeSystemRuntimePath(file.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        return new SystemRuntimeSummary(
            writes.Count,
            writes.Count(file => file.Kind == FileEventKind.Created),
            writes.Count(file => file.Kind == FileEventKind.Modified),
            writes.Count(file => file.Kind == FileEventKind.Deleted),
            writes.Count(file => file.Kind == FileEventKind.Renamed),
            examples);
    }

    private static bool IsGitInternalPath(string path) =>
        path.Replace('/', '\\').Contains("\\.git\\", StringComparison.OrdinalIgnoreCase);

    internal static string DescribeGitInternalPath(string path)
    {
        var normalized = path.Replace('/', '\\');
        var gitIndex = normalized.IndexOf("\\.git\\", StringComparison.OrdinalIgnoreCase);
        if (gitIndex < 0)
        {
            return path;
        }

        var relative = normalized[(gitIndex + 1)..];
        if (relative.StartsWith(".git\\objects\\", StringComparison.OrdinalIgnoreCase))
        {
            return ".git\\objects\\...";
        }

        if (relative.StartsWith(".git\\logs\\", StringComparison.OrdinalIgnoreCase))
        {
            return ".git\\logs\\...";
        }

        if (relative.StartsWith(".git\\refs\\", StringComparison.OrdinalIgnoreCase))
        {
            return ".git\\refs\\...";
        }

        return relative;
    }

    internal static string DescribeSystemRuntimePath(string path)
    {
        var normalized = path.Replace('/', '\\');
        var breadcrumb = "\\ProgramData\\Microsoft\\NetFramework\\BreadcrumbStore\\";
        var breadcrumbIndex = normalized.IndexOf(breadcrumb, StringComparison.OrdinalIgnoreCase);
        if (breadcrumbIndex >= 0)
        {
            return "ProgramData\\Microsoft\\NetFramework\\BreadcrumbStore\\...";
        }

        var usageLogs = "\\Microsoft\\CLR_v4.0\\UsageLogs\\";
        var usageLogIndex = normalized.IndexOf(usageLogs, StringComparison.OrdinalIgnoreCase);
        if (usageLogIndex >= 0)
        {
            return "Microsoft\\CLR_v4.0\\UsageLogs\\...";
        }

        var nativeImages = "\\assembly\\NativeImages_";
        var nativeImageIndex = normalized.IndexOf(nativeImages, StringComparison.OrdinalIgnoreCase);
        if (nativeImageIndex >= 0)
        {
            return "assembly\\NativeImages_..."; 
        }

        return normalized;
    }

    private static string RenderWatchScope(SessionReport session) =>
        session.WatchAll
            ? (session.WatchRoots.Count == 0
                ? "all live file paths"
                : $"all live file paths + snapshots under {string.Join("; ", session.WatchRoots)}")
            : string.Join("; ", session.WatchRoots);

    private static string RenderFilePath(FileEvent file) =>
        file.Kind == FileEventKind.Renamed && !string.IsNullOrWhiteSpace(file.RelatedPath)
            ? $"{file.Path} -> {file.RelatedPath}"
            : file.Path;

    private static string RenderProjectPath(ProjectFileChange file) =>
        file.Kind == FileEventKind.Renamed && !string.IsNullOrWhiteSpace(file.RelatedRelativePath)
            ? $"{file.RelativePath} -> {file.RelatedRelativePath}"
            : file.RelativePath;

    private static string NormalizeVisiblePath(string path)
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

    private static string Esc(string value) => WebUtility.HtmlEncode(value);

    private sealed record GitInternalSummary(int Total, int Created, int Modified, int Deleted, int Renamed, IReadOnlyList<string> Examples);
    private sealed record SystemRuntimeSummary(int Total, int Created, int Modified, int Deleted, int Renamed, IReadOnlyList<string> Examples);
}

internal static class CsvReport
{
    public static string RenderFiles(IReadOnlyList<FileEvent> events)
    {
        var builder = new StringBuilder();
        builder.AppendLine("kind,source,observed_at,process_id,process_name,category,size_before,size_after,size_delta,is_sensitive,path,related_path");
        foreach (var item in events)
        {
            builder.AppendLine(string.Join(",", [
                Csv(item.Kind.ToString()),
                Csv(item.Source),
                Csv(item.ObservedAt.ToString("O", CultureInfo.InvariantCulture)),
                Csv(item.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? ""),
                Csv(item.ProcessName ?? ""),
                Csv(item.Category),
                Csv(item.SizeBefore?.ToString(CultureInfo.InvariantCulture) ?? ""),
                Csv(item.SizeAfter?.ToString(CultureInfo.InvariantCulture) ?? ""),
                Csv(item.SizeDelta.ToString(CultureInfo.InvariantCulture)),
                Csv(item.IsSensitive.ToString(CultureInfo.InvariantCulture)),
                Csv(item.Path),
                Csv(item.RelatedPath ?? "")
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
