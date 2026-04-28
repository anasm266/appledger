using System.Globalization;
using System.Diagnostics;
using AppLedger;
using Xunit;

namespace AppLedger.Tests;

public sealed class NormalizationTests
{
    [Fact]
    public void RunOptions_AiCodeProfile_AppliesFriendlyCaptureDefaults()
    {
        var options = RunOptions.Parse(["notepad", "--profile", "ai-code"]);

        Assert.NotNull(options);
        Assert.True(options.WatchAll);
        Assert.False(options.CaptureReads);
        Assert.True(options.OpenReport);
        Assert.Equal(50_000, options.MaxEvents);
        Assert.Contains(Path.GetFullPath(Directory.GetCurrentDirectory()), options.WatchRoots);
        Assert.Contains("node_modules", options.PathFilter.Excludes);
        Assert.Contains("appledger-runs", options.PathFilter.Excludes);
    }

    [Fact]
    public void RunOptions_DefaultProfileName_AppliesAppSpecificProfile()
    {
        var options = RunOptions.Parse(["notepad"], defaultProfileName: "codex");

        Assert.NotNull(options);
        Assert.Equal("codex", options.ProfileName);
        Assert.True(options.WatchAll);
        Assert.False(options.CaptureReads);
        Assert.Contains("OpenAI\\Codex\\Cache", options.PathFilter.Excludes);
        Assert.Contains("node_repl\\node_modules", options.PathFilter.Excludes);
    }

    [Fact]
    public void RunOptions_NoOpen_DisablesAutomaticReportLaunch()
    {
        var options = RunOptions.Parse(["notepad", "--profile", "none", "--no-open"]);

        Assert.NotNull(options);
        Assert.False(options.OpenReport);
    }

    [Fact]
    public void AttachOptions_NoOpen_DisablesAutomaticReportLaunch()
    {
        var process = Process.GetCurrentProcess();
        var options = AttachOptions.Parse([process.Id.ToString(CultureInfo.InvariantCulture), "--profile", "none", "--no-open"]);

        Assert.NotNull(options);
        Assert.False(options.OpenReport);
    }

    [Fact]
    public void RecordingProfile_InferName_DetectsCommonAiApps()
    {
        var codex = new ProcessRecord(1, 0, "Codex.exe", @"C:\Program Files\WindowsApps\OpenAI.Codex\app\Codex.exe", "Codex.exe", At(1), At(1), At(2));
        var claude = new ProcessRecord(2, 0, "claude.exe", @"C:\Program Files\WindowsApps\Claude\app\Claude.exe", "claude.exe", At(1), At(1), At(2));
        var cursor = new ProcessRecord(3, 0, "Cursor.exe", @"C:\Users\Anas\AppData\Local\Programs\Cursor\Cursor.exe", "Cursor.exe", At(1), At(1), At(2));

        Assert.Equal("codex", RecordingProfile.InferName("codex", codex));
        Assert.Equal("claude", RecordingProfile.InferName("claude", claude));
        Assert.Equal("cursor", RecordingProfile.InferName("cursor", cursor));
        Assert.Equal("vscode", RecordingProfile.InferName("code", null));
        Assert.Equal("ai-code", RecordingProfile.InferName("unknown-editor", null));
    }

    [Fact]
    public void RunOptions_ParsesIncludeAndExcludePathFilters()
    {
        var options = RunOptions.Parse(["notepad", "--profile", "none", "--include", "src", "--exclude", "node_modules", "--exclude", ".git\\objects"]);

        Assert.NotNull(options);
        Assert.Contains("src", options.PathFilter.Includes);
        Assert.Contains("node_modules", options.PathFilter.Excludes);
        Assert.Contains(".git\\objects", options.PathFilter.Excludes);
    }

    [Fact]
    public void PathFilter_AppliesIncludesExcludesAndSegmentMatches()
    {
        var filter = PathFilter.From(["src"], ["node_modules", ".git\\objects"]);

        Assert.True(filter.Allows(@"C:\repo\src\App.cs"));
        Assert.False(filter.Allows(@"C:\repo\tests\AppTests.cs"));
        Assert.False(filter.Allows(@"C:\repo\src\node_modules\pkg\index.js"));
        Assert.True(filter.ExcludesDirectory(@"C:\repo\.git\objects"));
    }

    [Fact]
    public void RecordingProfile_UnknownProfile_ReturnsNull()
    {
        Assert.Null(RecordingProfile.Resolve("definitely-not-a-profile"));
    }

    [Fact]
    public void ProcessCatalog_SelectBestRoot_PrefersRootOfMatchingProcessTree()
    {
        var root = new ProcessRecord(400, 10, "Codex.exe", @"C:\Program Files\Codex\Codex.exe", "Codex.exe", At(1), At(1), At(2));
        var child = new ProcessRecord(100, 400, "Codex.exe", @"C:\Program Files\Codex\Codex.exe", "Codex.exe --type=renderer", At(2), At(2), At(3));
        var agent = new ProcessRecord(200, 400, "codex.exe", @"C:\Program Files\Codex\resources\codex.exe", "codex agent", At(3), At(3), At(4));
        var tool = new ProcessRecord(300, 200, "node_repl.exe", @"C:\Users\Anas\AppData\Local\OpenAI\Codex\bin\node_repl.exe", "node_repl", At(4), At(4), At(5));
        var unrelated = new ProcessRecord(50, 1, "appledger.exe", @"C:\repo\appledger.exe", "appledger record codex --watch .", At(5), At(5), At(6));

        var selected = ProcessCatalog.SelectBestRoot("codex", [child, agent, tool, unrelated, root]);

        Assert.NotNull(selected);
        Assert.Equal(root.ProcessId, selected.ProcessId);
    }

    [Fact]
    public void ProcessCatalog_SelectBestRoot_IgnoresAppLedgerCommandLineMatches()
    {
        var root = new ProcessRecord(400, 10, "Codex.exe", @"C:\Program Files\Codex\Codex.exe", "Codex.exe", At(1), At(1), At(2));
        var child = new ProcessRecord(100, 400, "Codex.exe", @"C:\Program Files\Codex\Codex.exe", "Codex.exe --type=renderer", At(2), At(2), At(3));
        var appLedger = new ProcessRecord(50, 1, "appledger.exe", @"C:\repo\src\AppLedger.Cli\bin\Debug\net8.0-windows\appledger.exe", "appledger record codex --watch .", At(5), At(5), At(6));
        var dotnet = new ProcessRecord(51, 1, "dotnet.exe", @"C:\Program Files\dotnet\dotnet.exe", "dotnet run --project src\\AppLedger.Cli -- record codex", At(5), At(5), At(6));
        var all = new[] { root, child, appLedger, dotnet };
        var matches = all.Where(process =>
            process.Name.Contains("codex", StringComparison.OrdinalIgnoreCase)
            || process.ExecutablePath?.Contains("codex", StringComparison.OrdinalIgnoreCase) == true
            || process.CommandLine?.Contains("codex", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();

        var selected = ProcessCatalog.SelectBestRoot("codex", all, matches);

        Assert.NotNull(selected);
        Assert.Equal(root.ProcessId, selected.ProcessId);
    }

    [Fact]
    public void NormalizeForSession_PromotesSnapshotCreateWithLiveModify()
    {
        var path = @"C:\Users\Anas\Documents\demo\created.txt";
        var created = SnapshotCreated(path, 42, observedAt: At(10));
        var modified = Live(FileEventKind.Modified, path, observedAt: At(12), processId: 101, processName: "codex.exe");

        var normalized = FileEventMerger.NormalizeForSession([created, modified]);

        var createdResult = Assert.Single(normalized);
        Assert.Equal(FileEventKind.Created, createdResult.Kind);
        Assert.Equal("normalized", createdResult.Source);
        Assert.Equal(At(12), createdResult.ObservedAt);
        Assert.Equal(101, createdResult.ProcessId);
        Assert.Equal("codex.exe", createdResult.ProcessName);
    }

    [Fact]
    public void NormalizeForSession_SynthesizesRenameTarget_AndRemovesCreatedTarget()
    {
        var oldPath = @"C:\Users\Anas\Documents\repo\rename-a.txt";
        var newPath = @"C:\Users\Anas\Documents\repo\rename-b.txt";
        var unrelated = @"C:\Users\Anas\Documents\repo\fresh-create.txt";

        var rename = Live(FileEventKind.Renamed, oldPath, observedAt: At(20), processId: 222, processName: "powershell.exe");
        var createdTarget = SnapshotCreated(newPath, 15, observedAt: At(21), processId: 222, processName: "powershell.exe");
        var otherCreated = SnapshotCreated(unrelated, 8, observedAt: At(19), processId: 222, processName: "powershell.exe");

        var normalized = FileEventMerger.NormalizeForSession([rename, createdTarget, otherCreated]);

        var renameResult = Assert.Single(normalized, file => file.Kind == FileEventKind.Renamed);
        Assert.Equal(newPath, renameResult.RelatedPath);
        Assert.DoesNotContain(normalized, file => file.Kind == FileEventKind.Created && file.Path.Equals(newPath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(normalized, file => file.Kind == FileEventKind.Created && file.Path.Equals(unrelated, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Merge_PrefersLiveDeleteOverSnapshotDelete()
    {
        var path = @"C:\Users\Anas\Documents\repo\deleted.txt";
        var live = Live(FileEventKind.Deleted, path, observedAt: At(30), processId: 77, processName: "cmd.exe");
        var snapshot = SnapshotDeleted(path, 10, observedAt: At(31));

        var merged = FileEventMerger.Merge([live], [snapshot]);

        var deleted = Assert.Single(merged);
        Assert.Equal(FileEventKind.Deleted, deleted.Kind);
        Assert.Equal("etw", deleted.Source);
    }

    [Fact]
    public void SessionActivityAnalyzer_BuildsCodexLikeSummary()
    {
        var tempFile = Live(FileEventKind.Modified, @"C:\Users\Anas\AppData\Local\Temp\work.tmp", observedAt: At(40), processId: 500, processName: "codex.exe");
        var sensitiveRead = Live(FileEventKind.Read, @"C:\Users\Anas\.gitconfig", observedAt: At(41), processId: 501, processName: "git.exe");
        var gitWrite = Live(FileEventKind.Modified, @"C:\Users\Anas\Documents\repo\.git\index", observedAt: At(42), processId: 501, processName: "git.exe");
        var files = FileEventMerger.NormalizeForSession([tempFile, sensitiveRead, gitWrite]);

        var ai = EmptyAi() with
        {
            Commands = new CommandSummary(Total: 30, PackageInstalls: 0, GitCommands: 30, TestCommands: 0, ShellCommands: 0, ScriptCommands: 0),
            SensitiveAccesses = [new SensitiveAccess(FileEventKind.Read, sensitiveRead.Path, sensitiveRead.Path, "etw", 501, "git.exe", sensitiveRead.ObservedAt)]
        };

        var findings = new List<Finding>
        {
            new("medium", "Sensitive path touched", $"read {sensitiveRead.Path}")
        };

        var network = new List<NetworkEvent>
        {
            new(501, "127.0.0.1", 12345, "140.82.112.3", 443, "Established", At(43), "lb-140-82-112-3-iad.github.com")
        };

        var overview = SessionActivityAnalyzer.Build([], true, files, network, ai, findings);

        Assert.Contains("Mostly temp churn", overview.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ran 30 git commands", overview.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("touched sensitive paths", overview.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("contacted 1 network endpoint", overview.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(overview.Buckets, bucket => bucket.Key == "temp-churn");
        Assert.Contains(overview.Buckets, bucket => bucket.Key == "git-metadata");
        Assert.Contains(overview.Buckets, bucket => bucket.Key == "sensitive-paths");
        Assert.Contains(overview.Buckets, bucket => bucket.Key == "network");
    }

    [Fact]
    public void SessionActivityAnalyzer_BuildsClaudeLikeSummary()
    {
        var cacheWrite = Live(FileEventKind.Modified, @"C:\Users\Anas\AppData\Roaming\Claude\IndexedDB\blob.data", observedAt: At(50), processId: 888, processName: "claude.exe");
        var files = FileEventMerger.NormalizeForSession([cacheWrite]);

        var network = new List<NetworkEvent>
        {
            new(888, "127.0.0.1", 22222, "65.8.20.77", 443, "Established", At(51), "server-65-8-20-77.phx50.r.cloudfront.net")
        };

        var overview = SessionActivityAnalyzer.Build([], true, files, network, EmptyAi(), []);

        Assert.Contains("Mostly app data / cache", overview.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(overview.Buckets, bucket => bucket.Key == "app-data-cache");
        Assert.Contains(overview.Buckets, bucket => bucket.Key == "network");
    }

    [Fact]
    public void HtmlReport_RendersBigPictureAndActivityBuckets()
    {
        var file = Live(FileEventKind.Modified, @"C:\Users\Anas\AppData\Local\Temp\session.tmp", observedAt: At(60), processId: 10, processName: "codex.exe");
        var files = FileEventMerger.NormalizeForSession([file]);
        var overview = SessionActivityAnalyzer.Build([], true, files, [], EmptyAi(), []);

        var session = new SessionReport(
            App: @"C:\Program Files\App\App.exe",
            Arguments: "",
            StartedAt: At(60),
            EndedAt: At(61),
            WatchRoots: [],
            WatchAll: true,
            Summary: new SessionSummary(0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0),
            FileEvents: files,
            Processes: [new ProcessRecord(10, 0, "codex.exe", @"C:\Program Files\App\App.exe", "\"app.exe\"", At(60), At(60), At(61))],
            NetworkEvents: [],
            RegistryEvents: [],
            Findings: [],
            TopFolders: [new FolderImpact(@"C:\Users\Anas\AppData\Local\Temp", 1, 0, "temp")],
            AiActivity: EmptyAi(),
            SnapshotErrors: [],
            ActivityOverview: overview);

        var html = HtmlReport.Render(session);

        Assert.Contains("Big Picture", html, StringComparison.Ordinal);
        Assert.Contains("Activity Buckets", html, StringComparison.Ordinal);
        Assert.Contains("Mostly temp churn", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("known bytes changed", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HtmlReport_LabelsWatchedRootChangesAndUnknownLiveBytes()
    {
        var watchRoot = @"C:\Users\Anas\Documents\repo";
        var file = Live(FileEventKind.Modified, Path.Combine(watchRoot, "scratch.tmp"), observedAt: At(62), processId: 10, processName: "codex.exe");
        var files = FileEventMerger.NormalizeForSession([file]);
        var ai = AiCodingAnalyzer.Build([watchRoot], files, []);
        var overview = SessionActivityAnalyzer.Build([watchRoot], true, files, [], ai, []);

        var session = new SessionReport(
            App: @"C:\Program Files\App\App.exe",
            Arguments: "",
            StartedAt: At(62),
            EndedAt: At(63),
            WatchRoots: [watchRoot],
            WatchAll: true,
            Summary: new SessionSummary(0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0),
            FileEvents: files,
            Processes: [new ProcessRecord(10, 0, "codex.exe", @"C:\Program Files\App\App.exe", "\"app.exe\"", At(62), At(62), At(63))],
            NetworkEvents: [],
            RegistryEvents: [],
            Findings: [],
            TopFolders: [],
            AiActivity: ai,
            SnapshotErrors: [],
            ActivityOverview: overview);

        var html = HtmlReport.Render(session);

        Assert.Contains("Watched Root Changes", html, StringComparison.Ordinal);
        Assert.Contains("watched-root paths changed", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unknown", html, StringComparison.Ordinal);
        Assert.Contains("Live ETW file events often do not include file-size deltas", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("project files changed", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HtmlReport_ShowsDisabledFileReadsWhenCaptureReadsIsOff()
    {
        var session = new SessionReport(
            App: @"C:\Program Files\App\App.exe",
            Arguments: "",
            StartedAt: At(65),
            EndedAt: At(66),
            WatchRoots: [],
            WatchAll: true,
            Summary: new SessionSummary(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0),
            FileEvents: [],
            Processes: [new ProcessRecord(10, 0, "codex.exe", @"C:\Program Files\App\App.exe", "\"app.exe\"", At(65), At(65), At(66))],
            NetworkEvents: [],
            RegistryEvents: [],
            Findings: [],
            TopFolders: [],
            AiActivity: EmptyAi(),
            SnapshotErrors: [],
            CaptureSettings: new SessionCaptureSettings("ai-code", WatchAll: true, CaptureReads: false, MaxEvents: 50_000, WriteSqlite: true));

        var html = HtmlReport.Render(session);

        Assert.Contains("file reads disabled", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("disabled by capture settings", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ai-code", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SessionReport_AttachesProcessIdentityToFileAndNetworkEvents()
    {
        var process = new ProcessRecord(
            123,
            45,
            "codex.exe",
            @"C:\Program Files\Codex\codex.exe",
            "codex run task",
            At(66),
            At(66),
            At(70));
        var file = Live(FileEventKind.Modified, @"C:\Users\Anas\Documents\repo\file.txt", observedAt: At(67), processId: 123, processName: "codex.exe");
        var network = new NetworkEvent(123, "127.0.0.1", 50000, "140.82.112.3", 443, "Established", At(68), "github.com");

        var session = SessionReport.Build(
            @"C:\Program Files\Codex\codex.exe",
            "",
            At(66),
            At(70),
            [],
            true,
            EmptySnapshot(),
            EmptySnapshot(),
            [file],
            [process],
            [network],
            []);

        var enrichedFile = Assert.Single(session.FileEvents);
        Assert.NotNull(enrichedFile.Process);
        Assert.NotNull(enrichedFile.Attribution);
        Assert.Equal(123, enrichedFile.Process.Pid);
        Assert.Equal(45, enrichedFile.Process.ParentPid);
        Assert.Equal(At(66), enrichedFile.Process.CreationTime);
        Assert.Equal(process.ExecutablePath, enrichedFile.Process.ExePath);
        Assert.Equal(process.CommandLineHash, enrichedFile.Process.CommandLineHash);
        Assert.Equal(process.ProcessInstanceKey, enrichedFile.Process.ProcessInstanceKey);
        Assert.Equal(At(66), enrichedFile.Process.FirstSeen);
        Assert.Equal(At(70), enrichedFile.Process.LastSeen);
        Assert.Equal(AttributionConfidence.High, enrichedFile.Attribution.Confidence);

        var enrichedNetwork = Assert.Single(session.NetworkEvents);
        Assert.NotNull(enrichedNetwork.Process);
        Assert.NotNull(enrichedNetwork.Attribution);
        Assert.Equal(123, enrichedNetwork.Process.Pid);
        Assert.Equal(process.CommandLineHash, enrichedNetwork.Process.CommandLineHash);
        Assert.Equal(AttributionConfidence.High, enrichedNetwork.Attribution.Confidence);
    }

    [Fact]
    public void SessionReport_UsesMediumAttributionWhenCreationTimeIsUnavailable()
    {
        var process = new ProcessRecord(321, 45, "tool.exe", null, "tool.exe", null, At(70), At(72));
        var file = Live(FileEventKind.Modified, @"C:\Users\Anas\Documents\repo\medium.txt", observedAt: At(71), processId: 321, processName: "tool.exe");

        var session = SessionReport.Build(
            "tool.exe",
            "",
            At(70),
            At(72),
            [],
            true,
            EmptySnapshot(),
            EmptySnapshot(),
            [file],
            [process],
            [],
            []);

        var enriched = Assert.Single(session.FileEvents);
        Assert.NotNull(enriched.Attribution);
        Assert.Equal(AttributionConfidence.Medium, enriched.Attribution.Confidence);
        Assert.Contains("creation time was unavailable", enriched.Attribution.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SessionReport_UsesLowAttributionForPidReuseStyleFallback()
    {
        var earlier = new ProcessRecord(777, 1, "first.exe", @"C:\first.exe", "first", At(1), At(1), At(2));
        var later = new ProcessRecord(777, 1, "second.exe", @"C:\second.exe", "second", At(90), At(90), At(92));
        var file = Live(FileEventKind.Modified, @"C:\Users\Anas\Documents\repo\low.txt", observedAt: At(50), processId: 777, processName: "unknown.exe");

        var session = SessionReport.Build(
            "app.exe",
            "",
            At(1),
            At(92),
            [],
            true,
            EmptySnapshot(),
            EmptySnapshot(),
            [file],
            [earlier, later],
            [],
            []);

        var enriched = Assert.Single(session.FileEvents);
        Assert.NotNull(enriched.Attribution);
        Assert.Equal(AttributionConfidence.Low, enriched.Attribution.Confidence);
        Assert.Contains("outside the observed event window", enriched.Attribution.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NetworkSummaryAnalyzer_GroupsByResolvedHostAndProcess()
    {
        var processes = new List<ProcessRecord>
        {
            new(100, 0, "git.exe", @"C:\Program Files\Git\bin\git.exe", "git fetch", At(70), At(70), At(71)),
            new(101, 0, "codex.exe", @"C:\Program Files\OpenAI\codex.exe", "codex", At(70), At(70), At(71))
        };

        var network = new List<NetworkEvent>
        {
            new(100, "127.0.0.1", 12000, "140.82.112.3", 443, "Established", At(70), "lb-140-82-112-3-iad.github.com"),
            new(101, "127.0.0.1", 12001, "140.82.112.4", 443, "Established", At(71), "lb-140-82-112-3-iad.github.com"),
            new(101, "127.0.0.1", 12002, "140.82.112.4", 443, "Established", At(72), "lb-140-82-112-3-iad.github.com")
        };

        var overview = NetworkSummaryAnalyzer.Build(network, processes);

        var destination = Assert.Single(overview.Destinations);
        Assert.Equal("lb-140-82-112-3-iad.github.com", destination.HostLabel);
        Assert.Equal(3, destination.ConnectionCount);
        Assert.Equal(2, destination.ProcessCount);
        Assert.Contains("git.exe", destination.Processes);
        Assert.Contains("codex.exe", destination.Processes);

        Assert.Equal(2, overview.Processes.Count);
        Assert.Contains(overview.Processes, item => item.ProcessName == "codex.exe" && item.ConnectionCount == 2);
    }

    [Fact]
    public void NetworkSummaryAnalyzer_UsesUnresolvedLabelForRawIp()
    {
        var processes = new List<ProcessRecord>
        {
            new(200, 0, "claude.exe", @"C:\Program Files\Claude\claude.exe", "claude", At(80), At(80), At(81))
        };

        var network = new List<NetworkEvent>
        {
            new(200, "127.0.0.1", 13000, "34.54.194.141", 443, "Established", At(80), null)
        };

        var overview = NetworkSummaryAnalyzer.Build(network, processes);

        var destination = Assert.Single(overview.Destinations);
        Assert.Equal("34.54.194.141 (unresolved)", destination.HostLabel);
        Assert.Equal("34.54.194.141", destination.DisplayAddress);
    }

    [Fact]
    public void HtmlReport_RendersNetworkSummarySection()
    {
        var file = Live(FileEventKind.Modified, @"C:\Users\Anas\AppData\Local\Temp\session.tmp", observedAt: At(90), processId: 10, processName: "codex.exe");
        var files = FileEventMerger.NormalizeForSession([file]);
        var overview = SessionActivityAnalyzer.Build([], true, files, [], EmptyAi(), []);
        var process = new ProcessRecord(10, 0, "codex.exe", @"C:\Program Files\App\App.exe", "\"app.exe\"", At(90), At(90), At(91));
        var networkEvents = new List<NetworkEvent>
        {
            new(10, "127.0.0.1", 14000, "140.82.112.3", 443, "Established", At(91), "lb-140-82-112-3-iad.github.com"),
            new(10, "127.0.0.1", 14001, "34.54.194.141", 443, "Established", At(92), null)
        };
        var networkOverview = NetworkSummaryAnalyzer.Build(networkEvents, [process]);

        var session = new SessionReport(
            App: @"C:\Program Files\App\App.exe",
            Arguments: "",
            StartedAt: At(90),
            EndedAt: At(91),
            WatchRoots: [],
            WatchAll: true,
            Summary: new SessionSummary(0, 0, 1, 0, 0, 0, 1, 0, 2, 0, 0),
            FileEvents: files,
            Processes: [process],
            NetworkEvents: networkEvents,
            RegistryEvents: [],
            Findings: [],
            TopFolders: [new FolderImpact(@"C:\Users\Anas\AppData\Local\Temp", 1, 0, "temp")],
            AiActivity: EmptyAi(),
            SnapshotErrors: [],
            ActivityOverview: overview,
            NetworkOverview: networkOverview);

        var html = HtmlReport.Render(session);

        Assert.Contains("Network Summary", html, StringComparison.Ordinal);
        Assert.Contains("34.54.194.141 (unresolved)", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lb-140-82-112-3-iad.github.com", html, StringComparison.OrdinalIgnoreCase);
    }

    private static AiCodingActivity EmptyAi() =>
        new(
            new ProjectChangeSummary(0, 0, 0, 0, 0),
            [],
            new CommandSummary(0, 0, 0, 0, 0, 0),
            [],
            [],
            [],
            []);

    private static FileEvent Live(FileEventKind kind, string path, DateTimeOffset observedAt, int processId, string processName, string? relatedPath = null) =>
        FileEvent.Live(kind, path, processId, processName, relatedPath) with
        {
            ObservedAt = observedAt
        };

    private static FileEvent SnapshotCreated(string path, long size, DateTimeOffset observedAt, int? processId = null, string? processName = null) =>
        FileEvent.Created(path, new FileState(size, observedAt.UtcDateTime)) with
        {
            ObservedAt = observedAt,
            ProcessId = processId,
            ProcessName = processName
        };

    private static FileEvent SnapshotDeleted(string path, long size, DateTimeOffset observedAt) =>
        FileEvent.Deleted(path, new FileState(size, observedAt.UtcDateTime)) with
        {
            ObservedAt = observedAt
        };

    private static FileSnapshot EmptySnapshot() =>
        new(At(0), [], [], []);

    private static DateTimeOffset At(int second) =>
        new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero).AddSeconds(second);
}
