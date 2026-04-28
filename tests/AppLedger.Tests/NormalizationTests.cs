using System.Globalization;
using AppLedger;
using Xunit;

namespace AppLedger.Tests;

public sealed class NormalizationTests
{
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

    private static DateTimeOffset At(int second) =>
        new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero).AddSeconds(second);
}
