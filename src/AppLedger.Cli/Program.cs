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
                "record" => await RecordAsync(args.Skip(1).ToArray()),
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

    private static async Task<int> RecordAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return Fail("Usage: appledger record <app|process search|pid> [--profile ai-code] [--watch <path>] [--out <dir>] [--timeout <seconds>]");
        }

        var target = args[0].Trim('"');
        if (ProcessCatalog.Resolve(target) is not null)
        {
            return await AttachAsync(args, defaultProfileName: "ai-code");
        }

        return await RunAsync(args, defaultProfileName: "ai-code");
    }

    private static async Task<int> RunAsync(string[] args, string? defaultProfileName = null)
    {
        var options = RunOptions.Parse(args, defaultProfileName);
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
            registryEvents,
            CaptureSettings(options.ProfileName, options.WatchAll, options.CaptureReads, options.MaxEvents, options.WriteSqlite));

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
        Console.WriteLine($"  File reads:     {RenderFileReadSummary(session)}");
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

    private static async Task<int> AttachAsync(string[] args, string? defaultProfileName = null)
    {
        var options = AttachOptions.Parse(args, defaultProfileName);
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
            registryEvents,
            CaptureSettings(options.ProfileName, options.WatchAll, options.CaptureReads, options.MaxEvents, options.WriteSqlite));

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
        Console.WriteLine($"  File reads:     {RenderFileReadSummary(session)}");
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

    private static SessionCaptureSettings CaptureSettings(string? profile, bool watchAll, bool captureReads, int? maxEvents, bool writeSqlite) =>
        new(profile, watchAll, captureReads, maxEvents, writeSqlite);

    private static string RenderFileReadSummary(SessionReport session) =>
        session.CaptureSettings?.CaptureReads == false
            ? "disabled by capture settings"
            : session.Summary.FilesRead.ToString("N0", CultureInfo.InvariantCulture);

    private static void PrintHelp()
    {
        Console.WriteLine("""
        AppLedger - a black box recorder for Windows apps.

        Usage:
          appledger apps [search]
          appledger ps [search]
          appledger record <app|process search|pid> [--profile ai-code] [--watch <path>] [--out <dir>] [--timeout <seconds>]
          appledger run <app name|alias|exe> [--args "<arguments>"] [--profile <name>] [--watch <path>] [--watch-all] [--no-reads] [--max-events <n>] [--no-sqlite] [--out <dir>] [--timeout <seconds>]
          appledger attach <pid|process search> [--profile <name>] [--watch <path>] [--watch-all] [--no-reads] [--max-events <n>] [--no-sqlite] [--out <dir>] [--timeout <seconds>]
          appledger report <session.json|session.sqlite> [--out <dir>] [--no-sqlite]
          appledger snapshot <output.json> --watch <path>
          appledger diff <before.json> <after.json>

        Examples:
          appledger apps code
          appledger record codex --watch .
          appledger attach codex --profile ai-code
          appledger run code --watch "C:\Users\Anas\Projects\demo-app"
          appledger ps codex
          appledger run "C:\Windows\System32\notepad.exe" --watch "%USERPROFILE%\Documents"
          appledger run "C:\Path\To\Code.exe" --watch "C:\Users\Anas\Projects\demo-app"

        Profiles:
          ai-code  Whole-app live capture, no file reads, 50,000 live file cap, current-directory snapshot
          none     No preset; use explicit flags

        Notes:
          Phase 1 uses live ETW file/process capture when elevated, samples IPv4 TCP
          connections, and keeps before/after snapshots as a fallback.
        """);
    }
}
