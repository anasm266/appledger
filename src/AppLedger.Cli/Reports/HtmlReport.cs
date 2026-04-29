namespace AppLedger;

internal static class HtmlReport
{
    public static string Render(SessionReport session)
    {
        var ai = AiCodingAnalyzer.Build(session.WatchRoots, session.FileEvents, session.Processes);
        var appName = WebUtility.HtmlEncode(Path.GetFileName(session.App));
        var activityOverview = session.ActivityOverview ?? SessionActivityAnalyzer.Build(session.WatchRoots, session.WatchAll, session.FileEvents, session.NetworkEvents, ai, session.Findings);
        var networkOverview = session.NetworkOverview ?? NetworkSummaryAnalyzer.Build(session.NetworkEvents, session.Processes);
        var rows = string.Join(Environment.NewLine, VisibleFileEvents(session.FileEvents).Select(RenderFileRow));
        var processes = string.Join(Environment.NewLine, session.Processes.Select(RenderProcessRow));
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
        var networkDestinations = string.Join(Environment.NewLine, networkOverview.Destinations.Select(RenderNetworkDestinationRow));
        var networkProcesses = string.Join(Environment.NewLine, networkOverview.Processes.Select(RenderNetworkProcessRow));
        var captureSettings = session.CaptureSettings ?? SessionCaptureSettings.Default(session.WatchAll);
        var captureSettingsRows = RenderCaptureSettingsRows(captureSettings);
        var knownBytes = RenderKnownBytes(session);
        var fileReadMetricValue = captureSettings.CaptureReads
            ? session.Summary.FilesRead.ToString("N0", CultureInfo.InvariantCulture)
            : "Off";
        var fileReadMetricLabel = captureSettings.CaptureReads
            ? "file reads"
            : "file reads disabled";
        var attribution = SummarizeAttribution(session);
        var attributionRows = RenderAttributionRows(attribution);
        var firstScreenCards = RenderFirstScreenCards(session, ai, networkOverview, attribution, captureSettings);
        var priorityFindings = RenderPriorityFindings(session.Findings);
        var summaryTone = SummaryTone(session.Findings);

        return $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>AppLedger Report - {{appName}}</title>
          <style>
            :root { color-scheme: light; --ink:#17202a; --muted:#627083; --line:#d8dee7; --bg:#f6f8fb; --panel:#fff; --accent:#1664d9; --ok:#157347; --warn:#b25b00; --bad:#a11919; }
            * { box-sizing:border-box; }
            body { margin:0; font-family:Segoe UI, Arial, sans-serif; color:var(--ink); background:var(--bg); }
            header { background:#101820; color:#fff; padding:26px 40px; }
            header h1 { margin:0; font-size:30px; }
            header p { color:#c7d1dc; margin:8px 0 0; }
            main { max-width:1180px; margin:0 auto; padding:28px 24px 60px; }
            section { margin:22px 0; }
            .grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(180px,1fr)); gap:12px; }
            .hero { background:var(--panel); border:1px solid var(--line); border-top:5px solid var(--ok); border-radius:8px; padding:22px; }
            .hero.medium { border-top-color:var(--warn); }
            .hero.high { border-top-color:var(--bad); }
            .hero h2 { font-size:24px; margin-bottom:8px; }
            .hero p { margin:0 0 12px; }
            .hero-grid { display:grid; grid-template-columns:minmax(260px,1.5fr) minmax(240px,1fr); gap:18px; align-items:start; }
            .summary-cards { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:10px; }
            .summary-card { border:1px solid var(--line); border-radius:8px; padding:12px; background:#fbfcfe; min-height:86px; }
            .summary-card strong { display:block; font-size:22px; line-height:1.15; }
            .summary-card span { color:var(--muted); font-size:12px; }
            .summary-card.ok { border-left:4px solid var(--ok); }
            .summary-card.medium { border-left:4px solid var(--warn); }
            .summary-card.high { border-left:4px solid var(--bad); }
            .status { display:inline-block; border-radius:999px; padding:4px 10px; font-size:12px; font-weight:700; margin-bottom:8px; background:#eaf6ef; color:var(--ok); }
            .status.medium { background:#fff2df; color:var(--warn); }
            .status.high { background:#fdecec; color:var(--bad); }
            .lede { font-size:17px; line-height:1.45; }
            .summary-list { margin:10px 0 0; padding-left:20px; color:#364454; }
            .priority { margin:12px 0 0; padding:12px; border:1px solid var(--line); border-radius:8px; background:#fbfcfe; }
            .priority ul { margin:8px 0 0; padding-left:18px; }
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
            @media (max-width: 760px) {
              header { padding:22px 24px; }
              main { padding:20px 16px 44px; }
              .hero-grid { grid-template-columns:1fr; }
              .summary-cards { grid-template-columns:1fr; }
            }
          </style>
        </head>
        <body>
          <header>
            <h1>AppLedger Report: {{appName}}</h1>
            <p>{{Esc(session.StartedAt.ToString("g"))}} - {{Esc(session.EndedAt.ToString("g"))}} - {{Esc(RenderWatchScope(session))}}</p>
          </header>
          <main>
            <section class="hero {{summaryTone}}">
              <div class="hero-grid">
                <div>
                  <span class="status {{summaryTone}}">{{Esc(SummaryStatusLabel(session.Findings))}}</span>
                  <h2>Big Picture</h2>
                  <p class="lede"><strong>{{Esc(activityOverview.Headline)}}</strong></p>
                  {{(activityOverview.Highlights.Count == 0 ? "<p class=\"muted\">No extra highlights were derived for this session.</p>" : $"<ul class=\"summary-list\">{activityHighlights}</ul>")}}
                  {{priorityFindings}}
                </div>
                <div class="summary-cards">
                  {{firstScreenCards}}
                </div>
              </div>
            </section>

            <section class="grid">
              <div class="metric"><strong>{{fileReadMetricValue}}</strong><span>{{fileReadMetricLabel}}</span></div>
              <div class="metric"><strong>{{session.Summary.FilesCreated:N0}}</strong><span>files created</span></div>
              <div class="metric"><strong>{{session.Summary.FilesModified:N0}}</strong><span>files modified</span></div>
              <div class="metric"><strong>{{session.Summary.FilesDeleted:N0}}</strong><span>files deleted</span></div>
              <div class="metric"><strong>{{session.Summary.FilesRenamed:N0}}</strong><span>files renamed</span></div>
              <div class="metric"><strong>{{knownBytes.Value}}</strong><span>{{knownBytes.Label}}</span></div>
              <div class="metric"><strong>{{session.Summary.CommandCount:N0}}</strong><span>commands captured</span></div>
              <div class="metric"><strong>{{session.Summary.NetworkConnectionCount:N0}}</strong><span>network endpoints</span></div>
            </section>
            {{(knownBytes.ShowNote ? "<p class=\"muted\">Live ETW file events often do not include file-size deltas, so byte totals only include known snapshot or enriched size data.</p>" : "")}}

            <section>
              <h2>Activity Buckets</h2>
              {{(activityOverview.Buckets.Count == 0 ? "<p class=\"muted\">No bucket summary was derived for this session.</p>" : $"<div class=\"panel\"><table><thead><tr><th>Bucket</th><th>Events</th><th>Unique Paths</th><th>Known Bytes</th><th>Examples</th></tr></thead><tbody>{activityBuckets}</tbody></table></div>")}}
            </section>

            <section>
              <h2>Risky Observations</h2>
              {{(session.Findings.Count == 0 ? "<p class=\"muted\">No risky observations from the Phase 1 analyzers.</p>" : $"<ul class=\"findings\">{findings}</ul>")}}
            </section>

            <section>
              <h2>AI Coding Activity</h2>
              <div class="grid">
                <div class="metric"><strong>{{ai.ProjectChanges.TotalChanged:N0}}</strong><span>watched-root paths changed</span></div>
                <div class="metric"><strong>{{ai.Commands.PackageInstalls:N0}}</strong><span>package installs</span></div>
                <div class="metric"><strong>{{ai.Commands.GitCommands:N0}}</strong><span>git commands</span></div>
                <div class="metric"><strong>{{ai.Commands.TestCommands:N0}}</strong><span>test commands</span></div>
                <div class="metric"><strong>{{ai.SensitiveAccesses.Count:N0}}</strong><span>sensitive accesses</span></div>
                <div class="metric"><strong>{{ai.ProcessGroups.Count:N0}}</strong><span>process groups</span></div>
              </div>
            </section>

            <section>
              <h2>Attribution Quality</h2>
              <div class="grid">
                <div class="metric"><strong>{{attribution.HighPercent:0.#}}%</strong><span>high confidence</span></div>
                <div class="metric"><strong>{{attribution.MediumPercent:0.#}}%</strong><span>medium confidence</span></div>
                <div class="metric"><strong>{{attribution.LowPercent:0.#}}%</strong><span>low confidence</span></div>
                <div class="metric"><strong>{{attribution.Total:N0}}</strong><span>attributed events</span></div>
              </div>
              <div class="panel"><table><thead><tr><th>Confidence</th><th>Events</th><th>Share</th></tr></thead><tbody>{{attributionRows}}</tbody></table></div>
            </section>

            <section>
              <h2>Capture Settings</h2>
              <div class="panel"><table><tbody>{{captureSettingsRows}}</tbody></table></div>
            </section>

            <section>
              <h2>Watched Root Changes</h2>
              {{(ai.ChangedProjectFiles.Count == 0 ? "<p class=\"muted\">No watched-root file changes detected.</p>" : $"<div class=\"panel\"><table><thead><tr><th>Action</th><th>Source</th><th>Category</th><th>Path</th></tr></thead><tbody>{projectFiles}</tbody></table></div>")}}
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
              <div class="panel"><table><thead><tr><th>Action</th><th>Source</th><th>Process Identity</th><th>Category</th><th>Delta</th><th>Path</th></tr></thead><tbody>{{rows}}</tbody></table></div>
            </section>

            <section>
              <h2>Child Processes and Commands</h2>
              <div class="panel"><table><thead><tr><th>PID</th><th>Parent</th><th>Created</th><th>First Seen</th><th>Last Seen</th><th>Name</th><th>Command Hash</th><th>Executable / Command</th></tr></thead><tbody>{{processes}}</tbody></table></div>
            </section>

            <section>
              <h2>Network Summary</h2>
              <div class="grid">
                <div class="metric"><strong>{{networkOverview.Destinations.Count:N0}}</strong><span>destination groups</span></div>
                <div class="metric"><strong>{{networkOverview.Processes.Count:N0}}</strong><span>network-active processes</span></div>
              </div>
              {{(networkOverview.Destinations.Count == 0
                  ? "<p class=\"muted\">No grouped network summary was derived for this session.</p>"
                  : $"""
              <div class="panel"><table><thead><tr><th>Destination</th><th>Address</th><th>Connections</th><th>Processes</th><th>Ports</th></tr></thead><tbody>{networkDestinations}</tbody></table></div>
              <div style="height:12px"></div>
              <div class="panel"><table><thead><tr><th>Process</th><th>Connections</th><th>Destinations</th><th>Examples</th></tr></thead><tbody>{networkProcesses}</tbody></table></div>
              """)}}
            </section>

            <section>
              <h2>Network</h2>
              <div class="panel"><table><thead><tr><th>Process Identity</th><th>Remote Host</th><th>Remote</th><th>State</th></tr></thead><tbody>{{network}}</tbody></table></div>
            </section>

            <p class="muted">Phase 1 uses ETW file/process events when elevated, live process/network sampling, and before/after file snapshots as fallback. Packet contents are not collected.</p>
          </main>
        </body>
        </html>
        """;
    }

    private static string RenderFileRow(FileEvent file) =>
        $"<tr><td>{Esc(file.Kind.ToString())}</td><td>{Esc(file.Source)}</td><td>{RenderProcessIdentity(file.ProcessId, file.ProcessName, file.Process, file.Attribution)}</td><td>{Esc(file.Category)}</td><td>{Format.Bytes(file.SizeDelta)}</td><td><code>{Esc(RenderFilePath(file))}</code></td></tr>";

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
        $"<tr><td>{RenderProcessIdentity(item.ProcessId, null, item.Process, item.Attribution)}</td><td>{Esc(NetworkResolver.DisplayHostLabel(item))}</td><td>{Esc(item.RemoteAddress)}:{item.RemotePort}</td><td>{Esc(item.State)}</td></tr>";

    private static string RenderFirstScreenCards(
        SessionReport session,
        AiCodingActivity ai,
        SessionNetworkOverview networkOverview,
        AttributionSummary attribution,
        SessionCaptureSettings captureSettings)
    {
        var riskCount = session.Findings.Count(finding => finding.Severity is "high" or "medium");
        var riskTone = session.Findings.Any(finding => finding.Severity == "high")
            ? "high"
            : riskCount > 0 ? "medium" : "ok";
        var riskValue = riskCount == 0 ? "Clear" : riskCount.ToString("N0", CultureInfo.InvariantCulture);
        var riskLabel = riskCount == 0 ? "no medium/high observations" : "medium/high observations";
        var projectLabel = session.WatchRoots.Count == 0
            ? "no watched root snapshot"
            : "watched-root paths changed";
        var commandLabel = BuildCommandCardLabel(ai.Commands);
        var networkLabel = networkOverview.Destinations.Count == 1 ? "destination group" : "destination groups";
        var attributionValue = attribution.Total == 0
            ? "N/A"
            : $"{attribution.HighPercent:0.#}%";
        var attributionLabel = attribution.Total == 0
            ? "no attributed events"
            : "high-confidence attribution";
        var readsLabel = captureSettings.CaptureReads
            ? "file reads captured"
            : "file reads intentionally off";

        var cards = new[]
        {
            RenderSummaryCard(riskValue, riskLabel, riskTone),
            RenderSummaryCard(ai.ProjectChanges.TotalChanged.ToString("N0", CultureInfo.InvariantCulture), projectLabel, "ok"),
            RenderSummaryCard(ai.Commands.Total.ToString("N0", CultureInfo.InvariantCulture), commandLabel, "ok"),
            RenderSummaryCard(networkOverview.Destinations.Count.ToString("N0", CultureInfo.InvariantCulture), networkLabel, "ok"),
            RenderSummaryCard(attributionValue, attributionLabel, attribution.HighPercent < 90 && attribution.Total > 0 ? "medium" : "ok"),
            RenderSummaryCard(captureSettings.Profile ?? "none", readsLabel, captureSettings.CaptureReads ? "ok" : "medium")
        };

        return string.Join(Environment.NewLine, cards);
    }

    private static string RenderSummaryCard(string value, string label, string tone) =>
        $"<div class=\"summary-card {Esc(tone)}\"><strong>{Esc(value)}</strong><span>{Esc(label)}</span></div>";

    private static string BuildCommandCardLabel(CommandSummary commands)
    {
        var parts = new List<string>();
        if (commands.GitCommands > 0)
        {
            parts.Add($"{commands.GitCommands:N0} git");
        }

        if (commands.PackageInstalls > 0)
        {
            parts.Add($"{commands.PackageInstalls:N0} package");
        }

        if (commands.TestCommands > 0)
        {
            parts.Add($"{commands.TestCommands:N0} test");
        }

        return parts.Count == 0
            ? "developer commands captured"
            : string.Join(", ", parts);
    }

    private static string RenderPriorityFindings(IReadOnlyList<Finding> findings)
    {
        var priority = findings
            .Where(finding => finding.Severity is "high" or "medium")
            .OrderBy(finding => finding.Severity == "high" ? 0 : 1)
            .Take(3)
            .ToList();

        if (priority.Count == 0)
        {
            return "<div class=\"priority\"><strong>Risk check</strong><p class=\"muted\">No medium or high observations from the current analyzers.</p></div>";
        }

        var items = string.Join(Environment.NewLine, priority.Select(finding =>
            $"<li><strong>{Esc(finding.Title)}</strong><br><span class=\"muted\">{Esc(finding.Detail)}</span></li>"));
        return $"<div class=\"priority\"><strong>Priority observations</strong><ul>{items}</ul></div>";
    }

    private static string SummaryTone(IReadOnlyList<Finding> findings)
    {
        if (findings.Any(finding => finding.Severity == "high"))
        {
            return "high";
        }

        return findings.Any(finding => finding.Severity == "medium")
            ? "medium"
            : "ok";
    }

    private static string SummaryStatusLabel(IReadOnlyList<Finding> findings)
    {
        if (findings.Any(finding => finding.Severity == "high"))
        {
            return "High-priority observations";
        }

        return findings.Any(finding => finding.Severity == "medium")
            ? "Review recommended"
            : "No medium/high observations";
    }

    private static string RenderProcessRow(ProcessRecord process)
    {
        var created = process.CreationDate?.ToString("O", CultureInfo.InvariantCulture) ?? "";
        var command = string.IsNullOrWhiteSpace(process.CommandLine)
            ? process.ExecutablePath ?? ""
            : process.CommandLine;
        return $"<tr><td>{process.ProcessId}</td><td>{process.ParentProcessId}</td><td>{Esc(created)}</td><td>{Esc(process.FirstSeen.ToString("O", CultureInfo.InvariantCulture))}</td><td>{Esc(process.LastSeen.ToString("O", CultureInfo.InvariantCulture))}</td><td>{Esc(process.Name)}</td><td><code>{Esc(ShortHash(process.CommandLineHash))}</code></td><td><code>{Esc(command)}</code></td></tr>";
    }

    private static string RenderProcessIdentity(int? processId, string? processName, ProcessIdentity? identity)
    {
        if (identity is null)
        {
            return processId is null
                ? "<span class=\"muted\">unknown</span>"
                : $"PID {processId}<br><span class=\"muted\">{Esc(processName ?? "identity unavailable")}</span>";
        }

        var created = identity.CreationTime?.ToString("O", CultureInfo.InvariantCulture) ?? "unknown creation";
        var exe = string.IsNullOrWhiteSpace(identity.ExePath) ? "unknown exe" : identity.ExePath;
        return $"""
            PID {identity.Pid} / PPID {identity.ParentPid}<br>
            <span class="muted">created {Esc(created)}</span><br>
            <code>{Esc(exe)}</code><br>
            <span class="muted">cmd {Esc(ShortHash(identity.CommandLineHash))}, seen {Esc(identity.FirstSeen.ToString("T", CultureInfo.InvariantCulture))}-{Esc(identity.LastSeen.ToString("T", CultureInfo.InvariantCulture))}</span>
            """;
    }

    private static string RenderProcessIdentity(int? processId, string? processName, ProcessIdentity? identity, Attribution? attribution)
    {
        var identityHtml = RenderProcessIdentity(processId, processName, identity);
        if (attribution is null)
        {
            return identityHtml;
        }

        return $"""
            {identityHtml}<br>
            <span class="tag">{Esc(attribution.Confidence.ToString().ToLowerInvariant())}</span>
            <span class="muted">{Esc(attribution.Reason)}</span>
            """;
    }

    private static string RenderNetworkDestinationRow(NetworkDestinationSummary item)
    {
        var processes = item.Processes.Count == 0
            ? "<span class=\"muted\">None</span>"
            : string.Join(", ", item.Processes.Select(Esc));
        var ports = string.Join(", ", item.Ports.Select(port => port.ToString(CultureInfo.InvariantCulture)));
        var addresses = string.Join(", ", item.Addresses);
        return $"<tr><td><strong>{Esc(item.HostLabel)}</strong><br><span class=\"muted\">{processes}</span></td><td><code>{Esc(addresses)}</code></td><td>{item.ConnectionCount:N0}</td><td>{item.ProcessCount:N0}</td><td>{Esc(ports)}</td></tr>";
    }

    private static string RenderNetworkProcessRow(NetworkProcessSummary item)
    {
        var examples = item.Destinations.Count == 0
            ? "<span class=\"muted\">None</span>"
            : string.Join("<br>", item.Destinations.Select(destination => $"<code>{Esc(destination)}</code>"));
        return $"<tr><td>{Esc(item.ProcessName)}</td><td>{item.ConnectionCount:N0}</td><td>{item.DestinationCount:N0}</td><td>{examples}</td></tr>";
    }

    private static string RenderActivityBucket(ActivityBucketSummary bucket)
    {
        var examples = bucket.Examples.Count == 0
            ? "<span class=\"muted\">None</span>"
            : string.Join("<br>", bucket.Examples.Select(example => $"<code>{Esc(example)}</code>"));

        return $"<tr><td><strong>{Esc(bucket.Label)}</strong><br><span class=\"muted\">{Esc(bucket.Description)}</span></td><td>{bucket.EventCount:N0}</td><td>{bucket.UniquePathCount:N0}</td><td>{Format.Bytes(bucket.BytesChanged)}</td><td>{examples}</td></tr>";
    }

    private static string RenderCaptureSettingsRows(SessionCaptureSettings settings)
    {
        var profile = string.IsNullOrWhiteSpace(settings.Profile) ? "none" : settings.Profile;
        var maxEvents = settings.MaxEvents is null ? "none" : settings.MaxEvents.Value.ToString("N0", CultureInfo.InvariantCulture);
        var includes = settings.IncludeFilters is { Count: > 0 }
            ? string.Join(", ", settings.IncludeFilters)
            : "none";
        var excludes = settings.ExcludeFilters is { Count: > 0 }
            ? string.Join(", ", settings.ExcludeFilters)
            : "none";
        var rows = new[]
        {
            ("Profile", profile),
            ("Whole-app live capture", settings.WatchAll ? "enabled" : "disabled"),
            ("File reads", settings.CaptureReads ? "captured" : "disabled by capture settings"),
            ("Live file event cap", maxEvents),
            ("Include filters", includes),
            ("Exclude filters", excludes),
            ("SQLite output", settings.WriteSqlite ? "enabled" : "disabled")
        };

        return string.Join(Environment.NewLine, rows.Select(row => $"<tr><th>{Esc(row.Item1)}</th><td>{Esc(row.Item2)}</td></tr>"));
    }

    private static KnownBytesDisplay RenderKnownBytes(SessionReport session)
    {
        if (session.Summary.BytesAddedOrChanged > 0)
        {
            return new KnownBytesDisplay(Format.Bytes(session.Summary.BytesAddedOrChanged), "known bytes changed", ShowNote: HasUnknownLiveSizeDeltas(session));
        }

        if (HasUnknownLiveSizeDeltas(session))
        {
            return new KnownBytesDisplay("Unknown", "known bytes changed", ShowNote: true);
        }

        return new KnownBytesDisplay("0 B", "known bytes changed", ShowNote: false);
    }

    private static bool HasUnknownLiveSizeDeltas(SessionReport session) =>
        session.FileEvents.Any(file =>
            file.Source.Equals("etw", StringComparison.OrdinalIgnoreCase)
            && file.Kind is FileEventKind.Created or FileEventKind.Modified or FileEventKind.Renamed
            && file.SizeBefore is null
            && file.SizeAfter is null);

    private static AttributionSummary SummarizeAttribution(SessionReport session)
    {
        var attributions = session.FileEvents
            .Select(file => file.Attribution)
            .Concat(session.NetworkEvents.Select(item => item.Attribution))
            .Where(item => item is not null)
            .Cast<Attribution>()
            .ToList();

        if (attributions.Count == 0)
        {
            return new AttributionSummary(0, 0, 0, 0);
        }

        return new AttributionSummary(
            attributions.Count,
            attributions.Count(item => item.Confidence == AttributionConfidence.High),
            attributions.Count(item => item.Confidence == AttributionConfidence.Medium),
            attributions.Count(item => item.Confidence == AttributionConfidence.Low));
    }

    private static string RenderAttributionRows(AttributionSummary summary)
    {
        var rows = new[]
        {
            ("High", summary.High, summary.HighPercent),
            ("Medium", summary.Medium, summary.MediumPercent),
            ("Low", summary.Low, summary.LowPercent)
        };

        return string.Join(Environment.NewLine, rows.Select(row => $"<tr><td>{Esc(row.Item1)}</td><td>{row.Item2:N0}</td><td>{row.Item3:0.#}%</td></tr>"));
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

    private static string ShortHash(string? hash) =>
        string.IsNullOrWhiteSpace(hash)
            ? "none"
            : hash[..Math.Min(12, hash.Length)];

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

    private sealed record AttributionSummary(int Total, int High, int Medium, int Low)
    {
        public double HighPercent => Percent(High);

        public double MediumPercent => Percent(Medium);

        public double LowPercent => Percent(Low);

        private double Percent(int value) =>
            Total == 0 ? 0 : value * 100.0 / Total;
    }

    private sealed record KnownBytesDisplay(string Value, string Label, bool ShowNote);

    private sealed record GitInternalSummary(int Total, int Created, int Modified, int Deleted, int Renamed, IReadOnlyList<string> Examples);
    private sealed record SystemRuntimeSummary(int Total, int Created, int Modified, int Deleted, int Renamed, IReadOnlyList<string> Examples);
}
