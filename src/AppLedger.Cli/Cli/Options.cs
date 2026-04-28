namespace AppLedger;

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

