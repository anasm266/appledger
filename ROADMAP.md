# AppLedger Roadmap

AppLedger should become a Windows black box recorder that answers one question clearly:

```txt
What did this app actually do during this session?
```

The product is not a raw event viewer. The differentiator is interpretation: per-session summary, risk observations, project-aware grouping, and cleanup guidance.

## Where It Is Now

Current state: credible CLI MVP.

Implemented:

- `apps` to discover recordable apps
- `apps --running` to pick already-running apps without hunting PIDs
- `ps` to search running processes
- `record` as the friendly default command that attaches when possible and launches otherwise
- `run` to launch and record an app
- `attach` to record an already-running process tree
- automatic `report.html` opening after `record`, `run`, and `attach`
- `--no-open` for scripted or terminal-only recordings
- `--profile ai-code` to choose sensible AI coding session defaults
- app-specific profiles: `codex`, `claude`, `cursor`, and `vscode`
- `record` automatically infers those profiles from the target app when `--profile` is omitted
- multi-use `--include` / `--exclude` path filters for live ETW events and snapshot diffs
- `--watch-all` to record whole-app live file activity
- `report` to regenerate artifacts from `session.json` or `session.sqlite`
- `snapshot` and `diff` for manual before/after workflows
- ETW live process and file capture when run elevated
- WMI process-tree polling fallback
- sampled IPv4 TCP endpoint capture
- cached DNS / reverse lookup hostname enrichment for network output
- grouped network summary by destination and process
- source split out of the original single-file implementation into CLI, collection, filesystem, analysis, model, output, registry, and report modules
- friendly `record` command and `ai-code` capture profile added
- `version`, `--version`, `doctor`, and `install` diagnostics added for release/install identity
- process-name recording now prefers the root of a matching process tree instead of an arbitrary child process
- running-app selection now groups Electron/helper processes under the best app root and deprioritizes AppLedger's own command line
- startup `Run` / `RunOnce` registry monitoring
- broader persistence-oriented registry monitoring for StartupApproved, services, scheduled task cache, protocol handlers, and file associations
- Startup folder write findings
- service image-path and scheduled task XML command/argument details
- readable service start/type labels and concise scheduled task trigger/condition summaries
- higher-signal risk observations for sensitive paths, PowerShell bypass, package installs, network tools, external endpoints, and outside-root writes
- Codex internal encoded-PowerShell parser, GitHub CLI metadata probes, .NET SDK workload-advertising churn, and GitHub metadata probe noise suppressed from risk findings while still appearing in raw process/network details
- HTML, JSON, CSV, SQLite, AI activity, and cleanup outputs
- conservative cleanup planner with likely-safe cache/temp candidates and review-only app-data candidates
- self-contained `win-x64` release packaging script with native ETW support files
- GitHub Actions release workflow for tag/manual builds and release assets
- first-screen whole-app summary via `Big Picture` and `Activity Buckets`
- polished first-screen HTML summary with risk state, priority observations, and compact session cards
- capture settings displayed in reports so disabled categories are clear
- project/user changes are labeled separately from raw watched-root, cache/temp, `.git`, and runtime activity
- byte totals are labeled as known bytes because live ETW events often lack size deltas
- process identity snapshots attached to file and network events for attribution
- attribution confidence and reason attached to file/network events
- active process-tree membership prunes stale reused PIDs before ETW/network filtering
- large-session controls: `--no-reads`, `--max-events`, `--no-sqlite`
- path filters are applied before event caps so excluded churn does not consume the session budget
- fixture-driven tests for normalization and summary logic

Recent Phase 1 fixes:

- attach-mode ETW process-tree sync for already-running apps
- delete and rename attribution working in elevated mode
- sensitive finding dedupe
- created-file lifecycle normalization for report/session output
- cleaner AI coding summaries with lower command/process noise
- `.git` and system-runtime noise grouped out of the main report tables
- rename destination synthesis in regenerated reports
- full-app recording validated against Codex and Claude sessions
- full-app report summary layer implemented
- large-session capture controls implemented
- test project added and passing against lifecycle / summary fixtures
- grouped network summary implemented and covered by tests
- Program.cs reduced to command orchestration, with collector/analyzer/reporting code moved into focused files
- reports distinguish disabled file reads from observed zero file reads
- reports show active include/exclude filters in capture settings
- process identity fields added across JSON, CSV, SQLite, and HTML report output
- attribution quality summary added to reports
- process-tree membership hardened against PID reuse false positives
- first-screen report summary moved above raw tables and tuned for quick scanning
- risk analyzer expanded for AI coding sessions and covered by tests
- persistence analyzer expanded beyond Run/RunOnce and covered by tests
- dedicated Persistence Summary added with clean-state wording
- scheduled task command/action and service image-path details surfaced when available
- structured persistence export added to session JSON
- service start/type labels and scheduled task trigger/condition summaries added
- release packaging added with `appledger-win-x64.zip`, native ETW support files, and SHA256 output
- GitHub release workflow added for tag-triggered release asset upload
- version/install/doctor commands added so stale PATH installs and missing native ETW support files are visible
- cleanup guidance added to reports and cleanup.ps1
- running app picker added for friendlier `record codex --watch .` style workflows
- app-specific AI profiles added on top of the generic `ai-code` defaults
- automatic report opening added with `--no-open` escape hatch
- report wording tightened for project/user changes, filtered AI profile noise, and known-byte totals
- Codex profile tuned against real sessions: `.codex` state, SQLite `etilqs_*` churn, PowerShell policy/startup probes, .NET telemetry/build/workload-advertising cache churn, `bin`/`obj` outputs, Codex internal encoded-PowerShell parser findings, GitHub CLI/API metadata probe findings, directory-only project events, and internal git introspection no longer dominate the AI activity story

Current proof point:

```txt
Record Codex Desktop, Claude Desktop, or another Electron app.
AppLedger shows app-data churn, project/user file changes even when an AI app edits under Documents outside the watched repo,
developer commands, sensitive file access, rename/delete activity, sampled endpoints,
and grouped process activity.
```

## Current Gaps

Still rough or incomplete:

- some file create attribution still relies on normalization instead of perfect live ETW creates
- process attribution is much stronger than the initial PID-only version, but long-session edge cases still need more real-world validation
- hostname correlation is opportunistic, not guaranteed for every endpoint
- command parsing is pragmatic, not exhaustive
- registry coverage focuses on high-value persistence locations, not broad whole-registry diffs
- scheduled task parsing focuses on concise action, trigger, and condition summaries, not full XML reproduction
- app-specific filter presets exist, but Claude, Cursor, and VS Code still need the same real-session tuning pass Codex just received
- large sessions still need test coverage and tuning
- no desktop UI yet
- release packaging currently targets `win-x64`; additional runtime zips can be added later
- `Analysis/Analyzers.cs` is still the largest file and can be split further once AI profiles mature

These are product polish and fidelity gaps, not viability gaps. The tool is already demonstrable.

## Next Up

Immediate next work should stay focused on making AI desktop app sessions more opinionated and easier to scan.

### 1. Tune AI Session Profiles Against Real Runs

Goal: make Codex/Cursor/VS Code/Claude sessions the strongest demo.

Built now:

- Codex profile tuned against a real Codex Desktop session
- Codex state (`.codex`), SQLite `etilqs_*` temp churn, app cache/log/sentry folders, AppLedger artifacts, and `.git` internals are filtered or separated from the main AI activity story
- PowerShell startup/profile probes, .NET telemetry/NuGet/MSBuild temp files, and `bin`/`obj` build outputs are filtered from Codex-profile noise
- source-like project/user files under Documents/Desktop/Downloads are detected even when the AI app edits outside the watched repo
- internal git introspection commands are filtered from Developer Commands
- internal git introspection commands are filtered from the AI process timeline
- Codex branch/status probes such as `for-each-ref --count=100` and blank helper git processes are filtered from the AI story
- Codex internal encoded-PowerShell parser, GitHub CLI metadata probes, GitHub metadata probes, and .NET SDK workload-advertising findings are suppressed from Risky Observations
- directory-only create/delete churn under watched roots is not counted as project file changes
- encoded PowerShell bootstrap commands are demoted from medium risk and hidden from the developer-command summary
- command grouping by high-level action

Next build:

- tune Claude/Cursor/VS Code filter presets from longer real reports
- improved sensitive path reporting
- cleaner process summary / process tree presentation

Success looks like:

```txt
Project/user paths changed: 6
Developer commands: 6
Sensitive paths touched: .env
Tests run: 2
```

### 2. Dedicated Persistence Summary

Goal: make persistence as scannable as network and AI activity.

Built now:

- startup registry checks
- StartupApproved registry checks
- Startup folder write checks
- service registry checks
- scheduled task cache checks
- scheduled task XML `Exec` command/argument extraction
- scheduled task trigger/condition summaries
- service image-path, start-mode, and type summaries
- protocol handler checks
- file association checks
- dedicated report section that says "Added no startup persistence" when clean
- structured persistence export block in `session.json`
- safer cache/temp cleanup classification
- clearer `cleanup.ps1` review comments

Next build:

- full scheduled task XML details only if the concise summary proves insufficient
- persistence-specific SQLite table if querying by persistence type becomes useful

Success looks like the first screen answering:

```txt
Big picture:
- touched 8 project/user paths
- read .env
- spawned PowerShell
- ran 6 git commands
- added no startup persistence
- left 800 MB likely cache
```

## Phase 2

Goal: strong Windows observability beyond the MVP.

Build:

- deeper registry snapshot/diff for high-value locations
- deeper scheduled task action extraction
- richer service configuration summaries
- protocol handler and file association summaries
- USN journal fallback for missed file changes
- deeper process-instance history for long sessions and exited/reused processes

This is where AppLedger stops being "useful CLI prototype" and becomes a technically serious Windows introspection tool.

## Phase 3

Goal: make the report the star magnet and the CLI comfortable to use.

Build:

- sharper HTML report structure
- better top-level summary cards
- stronger process-tree and timeline sections
- more opinionated cleanup guidance
- better export ergonomics
- signed binaries and broader runtime packaging if needed
- lightweight local UI once collector/report behavior is stable

The rule for this phase:

```txt
Open report.html and understand the session in under 30 seconds.
```

## Phase 4

Goal: differentiators that make AppLedger more than a readable recorder.

Build:

- AI agent mode tuned specifically for desktop coding tools
- uninstall / leftover analysis
- session comparison
- optional background collector service
- optional higher-fidelity driver-backed mode later
- optional guard mode later

Guard mode is explicitly later. Observation first.

## End State

By the end, AppLedger should be able to tell a user:

```txt
This app edited these files, created this cache, ran these commands, connected to
these destinations, touched these sensitive paths, added or did not add persistence,
and left behind these cleanup opportunities.
```

Best final demo:

```txt
Record Codex Desktop editing a repo.

AppLedger shows:
- project files changed
- package installs
- tests run
- git commands
- PowerShell bypass or encoded-shell observations when meaningful
- .env access
- network destinations
- startup registry unchanged
- cleanup available
```
