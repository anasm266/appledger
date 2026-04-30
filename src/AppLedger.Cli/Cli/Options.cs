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
    PathFilter PathFilter,
    bool WriteSqlite,
    bool OpenReport,
    string? ProfileName,
    TimeSpan? Timeout)
{
    public static RunOptions? Parse(string[] args)
        => Parse(args, defaultProfileName: null);

    public static RunOptions? Parse(string[] args, string? defaultProfileName)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: appledger run <app name|alias|exe> [--args \"...\"] [--profile <name>] [--watch <path>] [--watch-all] [--include <path-or-pattern>] [--exclude <path-or-pattern>] [--no-reads] [--max-events <n>] [--no-sqlite] [--no-open] [--out <dir>]");
            return null;
        }

        var profile = RecordingProfile.Resolve(Cli.GetOption(args, "--profile") ?? defaultProfileName);
        if (profile is null)
        {
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
        var hadExplicitWatchRoots = watchRoots.Count > 0;
        var watchAll = Cli.HasFlag(args, "--watch-all") || profile.WatchAll;
        var captureReads = !(Cli.HasFlag(args, "--no-reads") || profile.DisableReads);
        var writeSqlite = !Cli.HasFlag(args, "--no-sqlite");
        var openReport = !Cli.HasFlag(args, "--no-open");
        var maxEvents = Cli.GetPositiveIntOption(args, "--max-events") ?? profile.MaxEvents;
        var pathFilter = Cli.BuildPathFilter(args, profile);

        if (watchRoots.Count == 0 && (profile.IncludeCurrentDirectorySnapshot || !watchAll))
        {
            watchRoots.Add(Directory.GetCurrentDirectory());
        }

        if (!hadExplicitWatchRoots && watchRoots.Count == 1 && !watchAll && profile.IncludeTempSnapshot)
        {
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
            pathFilter,
            writeSqlite,
            openReport,
            profile.Name,
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
    PathFilter PathFilter,
    bool WriteSqlite,
    bool OpenReport,
    string? ProfileName,
    TimeSpan? Timeout)
{
    public static AttachOptions? Parse(string[] args)
        => Parse(args, defaultProfileName: null);

    public static AttachOptions? Parse(string[] args, string? defaultProfileName)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: appledger attach <pid|process search> [--profile <name>] [--watch <path>] [--watch-all] [--include <path-or-pattern>] [--exclude <path-or-pattern>] [--no-reads] [--max-events <n>] [--no-sqlite] [--no-open] [--out <dir>] [--timeout <seconds>]");
            return null;
        }

        var profile = RecordingProfile.Resolve(Cli.GetOption(args, "--profile") ?? defaultProfileName);
        if (profile is null)
        {
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
        var hadExplicitWatchRoots = watchRoots.Count > 0;
        var watchAll = Cli.HasFlag(args, "--watch-all") || profile.WatchAll;
        var captureReads = !(Cli.HasFlag(args, "--no-reads") || profile.DisableReads);
        var writeSqlite = !Cli.HasFlag(args, "--no-sqlite");
        var openReport = !Cli.HasFlag(args, "--no-open");
        var maxEvents = Cli.GetPositiveIntOption(args, "--max-events") ?? profile.MaxEvents;
        var pathFilter = Cli.BuildPathFilter(args, profile);

        if (watchRoots.Count == 0 && (profile.IncludeCurrentDirectorySnapshot || !watchAll))
        {
            watchRoots.Add(Directory.GetCurrentDirectory());
        }

        if (!hadExplicitWatchRoots && watchRoots.Count == 1 && !watchAll && profile.IncludeTempSnapshot)
        {
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

        return new AttachOptions(root, output, watchRoots, watchAll, captureReads, maxEvents, pathFilter, writeSqlite, openReport, profile.Name, timeout);
    }

}

internal sealed record RecordingProfile(
    string Name,
    bool WatchAll,
    bool DisableReads,
    int? MaxEvents,
    bool IncludeCurrentDirectorySnapshot,
    bool IncludeTempSnapshot,
    IReadOnlyList<string> IncludeFilters,
    IReadOnlyList<string> ExcludeFilters)
{
    private static readonly string[] CommonAiCodeExcludes =
    [
        "node_modules",
        ".git\\objects",
        ".git\\logs",
        "artifacts",
        "appledger-runs",
        "GPUCache",
        "Code Cache",
        "Cache_Data",
        "DawnCache",
        "ShaderCache",
        "GrShaderCache",
        "Crashpad",
        "etilqs_*",
        "__PSScriptPolicyTest_*",
        "StartupProfileData-NonInteractive",
        ".dotnet\\TelemetryStorageService",
        "NuGet\\v3-cache",
        "MSBuildTemp*",
        "CASESENSITIVETEST*",
        "Microsoft.NET.Workload_*",
        "bin",
        "obj"
    ];

    public static readonly RecordingProfile None = new(
        "none",
        WatchAll: false,
        DisableReads: false,
        MaxEvents: null,
        IncludeCurrentDirectorySnapshot: false,
        IncludeTempSnapshot: true,
        IncludeFilters: [],
        ExcludeFilters: []);

    public static readonly RecordingProfile AiCode = new(
        "ai-code",
        WatchAll: true,
        DisableReads: true,
        MaxEvents: 50_000,
        IncludeCurrentDirectorySnapshot: true,
        IncludeTempSnapshot: false,
        IncludeFilters: [],
        ExcludeFilters: CommonAiCodeExcludes);

    public static readonly RecordingProfile Codex = AiCode with
    {
        Name = "codex",
        ExcludeFilters = CommonAiCodeExcludes.Concat([
            "OpenAI\\Codex\\logs",
            "OpenAI\\Codex\\Cache",
            "Codex\\sentry",
            "node_repl\\node_modules",
            ".codex"
        ]).ToArray()
    };

    public static readonly RecordingProfile Claude = AiCode with
    {
        Name = "claude",
        ExcludeFilters = CommonAiCodeExcludes.Concat([
            "Claude\\logs",
            "Claude\\Cache",
            "Claude\\Code Cache",
            "Claude\\GPUCache"
        ]).ToArray()
    };

    public static readonly RecordingProfile Cursor = AiCode with
    {
        Name = "cursor",
        ExcludeFilters = CommonAiCodeExcludes.Concat([
            "Cursor\\CachedData",
            "Cursor\\CachedExtensionVSIXs",
            "Cursor\\CachedProfilesData",
            "Cursor\\logs"
        ]).ToArray()
    };

    public static readonly RecordingProfile VsCode = AiCode with
    {
        Name = "vscode",
        ExcludeFilters = CommonAiCodeExcludes.Concat([
            "Code\\CachedData",
            "Code\\CachedExtensionVSIXs",
            "Code\\CachedProfilesData",
            "Code\\logs"
        ]).ToArray()
    };

    public static RecordingProfile? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return None;
        }

        return name.Trim().ToLowerInvariant() switch
        {
            "none" => None,
            "ai-code" or "ai" or "coding" => AiCode,
            "codex" or "openai-codex" => Codex,
            "claude" or "claude-desktop" => Claude,
            "cursor" => Cursor,
            "vscode" or "vs-code" or "code" => VsCode,
            _ => Unknown(name)
        };
    }

    public static string InferName(string target, ProcessRecord? process = null)
    {
        var haystack = string.Join(
            " ",
            target,
            process?.Name,
            process?.ExecutablePath,
            process?.CommandLine);

        if (haystack.Contains("codex", StringComparison.OrdinalIgnoreCase)) return Codex.Name;
        if (haystack.Contains("claude", StringComparison.OrdinalIgnoreCase)) return Claude.Name;
        if (haystack.Contains("cursor", StringComparison.OrdinalIgnoreCase)) return Cursor.Name;
        if (haystack.Contains("Microsoft VS Code", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("\\Code.exe", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("Code.exe", StringComparison.OrdinalIgnoreCase)
            || target.Equals("code", StringComparison.OrdinalIgnoreCase)
            || target.Equals("vscode", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains(" vscode ", StringComparison.OrdinalIgnoreCase)) return VsCode.Name;

        return AiCode.Name;
    }

    private static RecordingProfile? Unknown(string name)
    {
        Console.Error.WriteLine($"Unknown profile: {name}");
        Console.Error.WriteLine("Known profiles: ai-code, codex, claude, cursor, vscode, none");
        return null;
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

    public static PathFilter BuildPathFilter(IReadOnlyList<string> args, RecordingProfile profile)
    {
        var includes = profile.IncludeFilters.Concat(GetRepeatedOption(args, "--include"));
        var excludes = profile.ExcludeFilters.Concat(GetRepeatedOption(args, "--exclude"));
        return PathFilter.From(includes, excludes);
    }
}

