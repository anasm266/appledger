# AppLedger

A black box recorder for Windows apps.

AppLedger records what an app did during a session and turns it into a readable report: files touched, child processes, command lines, sampled network endpoints, sensitive path access, and cleanup hints.

The product angle is not "ProcMon clone." ProcMon shows events. AppLedger explains a session.

## What Works Now

Current Phase 1 capabilities:

- record with a friendly default command: `record <app|process> --watch .`
- record a launched app with `run`
- attach to an already-running app with `attach`
- apply capture presets with `--profile ai-code`, `codex`, `claude`, `cursor`, or `vscode`
- discover easy app aliases with `apps`
- list running apps as attachable process groups with `apps --running`
- search running processes with `ps`
- record whole-app live file activity with `--watch-all`
- filter noisy paths before capture/export with multi-use `--include` and `--exclude`
- automatically pick app-specific AI profiles for Codex, Claude, Cursor, and VS Code when using `record`
- capture live file and process events with ETW when run elevated
- fall back to before/after watched-folder snapshots when ETW is unavailable
- sample IPv4 TCP endpoints by process ID
- enrich network endpoints with cached DNS / reverse lookup hostnames when possible
- group network activity by destination and process
- capture command lines from the observed process tree
- detect common startup `Run` and `RunOnce` registry changes
- summarize whole-app sessions with a top-level "Big Picture" and activity buckets
- show capture settings in reports so disabled categories such as file reads are explicit
- attach process identity snapshots to file and network events for stronger attribution
- show attribution confidence and reason for file/network events
- keep the CLI implementation split by collector, filesystem, analysis, output, and report responsibilities
- persist sessions as JSON and SQLite
- regenerate reports from `session.json` or `session.sqlite`
- generate AI-session sections for project file changes, developer commands, sensitive access, and process timeline
- normalize rename targets in regenerated reports and display renames as `old -> new`
- group `.git` internals and runtime bookkeeping out of the main report tables
- control large sessions with `--no-reads`, `--max-events <n>`, and `--no-sqlite`
- keep noisy defaults out of AI sessions, including `node_modules`, `.git\objects`, `.git\logs`, AppLedger output folders, common cache folders, and app-specific cache/log folders
- cover normalization and summary logic with unit tests

Generated artifacts:

- `report.html`
- `session.json`
- `session.sqlite`
- `touched-files.csv`
- `commands.json`
- `ai-activity.json`
- `cleanup.ps1`

Attribution fields are included in process records and attached to file/network events when a matching process is known:

- `pid`
- `parent_pid`
- `creation_time`
- `process_instance_key`
- `exe_path`
- `command_line_hash`
- `first_seen`
- `last_seen`
- `attribution_confidence`
- `attribution_reason`

Attribution confidence is currently:

- `high` when PID matches a known process instance with creation time and event-window evidence
- `medium` when PID matches a known session process but creation time is unavailable
- `low` when AppLedger falls back to a weaker PID/window match or cannot verify the PID

## Source Layout

The CLI is intentionally still one executable project, but the implementation is split by responsibility:

- `src/AppLedger.Cli/Program.cs` - command dispatch and session orchestration
- `src/AppLedger.Cli/Cli/` - option parsing and CLI helpers
- `src/AppLedger.Cli/Collection/` - ETW, WMI process sampling, TCP sampling, process/app discovery
- `src/AppLedger.Cli/FileSystem/` - file snapshots, diffs, events, and event normalization
- `src/AppLedger.Cli/Registry/` - registry snapshot/diff collection
- `src/AppLedger.Cli/Analysis/` - path classification, findings, AI activity, activity buckets, network summaries
- `src/AppLedger.Cli/Model/` - session/report data models
- `src/AppLedger.Cli/Outputs/` - artifact writing, JSON, and SQLite persistence
- `src/AppLedger.Cli/Reports/` - HTML, CSV, and cleanup script rendering
- `tests/AppLedger.Tests/` - fixture-driven tests for normalization and summary behavior

## Current Behavior

When AppLedger is run from an elevated terminal, it uses kernel ETW for live process and file events. That path now works for:

- file reads
- file deletes
- file renames
- many file creates and writes
- attach-mode process trees such as Codex, Cursor, or VS Code

When ETW is unavailable, AppLedger still records a useful session by:

- polling the process tree with WMI
- snapshotting watched roots before and after the session
- diffing the filesystem state afterward

The report also normalizes some raw event noise into something closer to user intent. For example, a file that was clearly created during the session and then written to is reported as a created file instead of a split `snapshot created` plus `ETW modified` story.

The best current recording modes are:

- `record <app|process> --watch .` for the default user flow
- `attach <process> --profile ai-code` for already-running AI coding tools
- `--watch-all` for "what did this app touch anywhere"
- `--watch <path>` for "what changed in this repo/folder"
- both together if you want whole-app activity plus a cleaner project diff
- `--include <path-or-pattern>` and `--exclude <path-or-pattern>` to filter noisy paths before ETW events are counted or snapshot diffs are generated

The best current control flags for noisy sessions are:

- `--no-reads` to drop the highest-volume ETW category
- `--max-events <n>` to stop once the session reaches a live event cap
- `--no-sqlite` to skip `session.sqlite` when you only want HTML/JSON/CSV
- `--exclude node_modules` or `--exclude .git\objects` to keep high-volume folders out of reports and event caps

Profiles bundle those flags for normal use. The `record` command infers these profiles from the target app unless `--profile` is provided:

- `--profile ai-code` enables whole-app live capture, disables file reads, caps live file events at `50,000`, snapshots the current directory, and excludes common dependency/cache/output churn
- `--profile codex` adds Codex-specific cache/log excludes
- `--profile claude` adds Claude-specific cache/log excludes
- `--profile cursor` adds Cursor-specific cache/log excludes
- `--profile vscode` adds VS Code-specific cache/log excludes
- `--profile none` disables presets and leaves behavior to explicit flags

When a profile disables a category, the report labels it as disabled. For example, `ai-code` reports file reads as `Off` / `file reads disabled` instead of implying AppLedger observed zero reads.
Active include/exclude filters are also shown in the report capture settings.

## Quick Start

Prerequisites:

- Windows
- .NET 8 SDK
- elevated PowerShell if you want live ETW file/process capture

Find an app by alias or search:

```powershell
dotnet run --project src\AppLedger.Cli -- apps code
dotnet run --project src\AppLedger.Cli -- apps cursor
dotnet run --project src\AppLedger.Cli -- apps --running codex
```

For day-to-day testing, prefer the published binary:

```powershell
dotnet publish src\AppLedger.Cli\AppLedger.Cli.csproj -c Release -o artifacts\publish-test
```

Record an app you launch:

```powershell
.\artifacts\publish-test\appledger.exe record code --watch .
```

Record with extra path filtering:

```powershell
.\artifacts\publish-test\appledger.exe record codex --watch . `
  --exclude node_modules `
  --exclude .git\objects
```

Attach to an app that is already running:

```powershell
dotnet run --project src\AppLedger.Cli -- ps codex
dotnet run --project src\AppLedger.Cli -- apps --running codex

.\artifacts\publish-test\appledger.exe record codex --watch .
```

Record the full app, not just one watched root:

```powershell
.\artifacts\publish-test\appledger.exe attach codex --profile ai-code
```

Record the full app and also keep a repo snapshot diff:

```powershell
.\artifacts\publish-test\appledger.exe attach codex --profile ai-code --watch .
```

Regenerate a report from a saved session:

```powershell
.\artifacts\publish-test\appledger.exe report artifacts\codex-self\session.sqlite `
  --out artifacts\codex-self-regenerated
```

Take manual snapshots and diff them:

```powershell
dotnet run --project src\AppLedger.Cli -- snapshot before.json --watch "."
# run something
dotnet run --project src\AppLedger.Cli -- snapshot after.json --watch "."
dotnet run --project src\AppLedger.Cli -- diff before.json after.json
```

Run the tests:

```powershell
dotnet test AppLedger.slnx
```

## CLI

```text
appledger apps [search]
appledger apps --running [search]
appledger ps [search]
appledger record <app|process search|pid> [--profile ai-code|codex|claude|cursor|vscode|none] [--watch <path>] [--include <path-or-pattern>] [--exclude <path-or-pattern>] [--out <dir>] [--timeout <seconds>]
appledger run <app name|alias|exe> [--args "<arguments>"] [--profile <name>] [--watch <path>] [--watch-all] [--include <path-or-pattern>] [--exclude <path-or-pattern>] [--no-reads] [--max-events <n>] [--no-sqlite] [--out <dir>] [--timeout <seconds>]
appledger attach <pid|process search> [--profile <name>] [--watch <path>] [--watch-all] [--include <path-or-pattern>] [--exclude <path-or-pattern>] [--no-reads] [--max-events <n>] [--no-sqlite] [--out <dir>] [--timeout <seconds>]
appledger report <session.json|session.sqlite> [--out <dir>] [--no-sqlite]
appledger snapshot <output.json> --watch <path> [--include <path-or-pattern>] [--exclude <path-or-pattern>]
appledger diff <before.json> <after.json>
```

Useful examples:

```powershell
appledger record codex --watch .
appledger record claude --watch .
appledger record cursor --watch .
appledger record code --watch .
appledger apps --running codex
appledger attach codex --profile ai-code
appledger record codex --watch . --exclude node_modules --exclude .git\objects

dotnet run --project src\AppLedger.Cli -- run "C:\Windows\System32\cmd.exe" `
  --args "/c npm test" `
  --watch "."

dotnet run --project src\AppLedger.Cli -- run notepad `
  --watch "$env:USERPROFILE\Documents"
```

## AI App Demo

AppLedger is already useful for desktop coding agents and editors.

Example workflow:

1. Find the running app:

```powershell
dotnet run --project src\AppLedger.Cli -- ps codex
dotnet run --project src\AppLedger.Cli -- apps --running codex
```

2. Attach to the root PID from an elevated terminal:

```powershell
.\artifacts\publish-test\appledger.exe record codex --watch .
```

`record` prefers an already-running process and falls back to launching an app. When several matching processes exist, AppLedger picks the root of the matching process tree so commands like `record codex --watch .` include child agent/helper processes. It infers app-specific profiles for Codex, Claude, Cursor, and VS Code; otherwise it falls back to `ai-code`. These profiles enable whole-app live capture, disable high-volume file reads, cap live file events at `50,000`, and snapshot the current directory for project diffs.

`apps --running <search>` is the friendlier process picker. It groups matching processes under the app root, shows child-process counts, and prints a ready-to-run `record` command so users do not need to hunt through Electron helper PIDs.

Path filters run before live events are counted against `--max-events` and before watched-root snapshots are diffed. Plain names match path segments such as `node_modules`; relative paths such as `.git\objects` match that path segment anywhere; rooted paths limit filtering to that exact tree; wildcards such as `*.tmp` are accepted.

3. Use the app normally, then open `report.html`.

What the report can now surface for AI sessions:

- changed project files
- shell and git commands
- sensitive file access such as `.env`
- rename and delete activity
- outbound endpoints with hostname enrichment when available
- grouped network destinations and network-active processes
- grouped process activity
- whole-app temp/cache churn vs project changes
- first-screen session summary via `Big Picture` and `Activity Buckets`

## Known Limits

What is not done yet:

- network byte counts
- packet contents
- broad registry diffs beyond current startup keys
- scheduled tasks, services, protocol handlers, and file associations
- GUI desktop app
- guard/block mode
- kernel driver or minifilter collector

What still needs refinement:

- some file-create attribution still depends on normalization rather than a perfect live ETW create event for every path
- DNS correlation is opportunistic, not guaranteed for every endpoint
- full-app mode still produces very large event volumes and will benefit from more path filtering controls
- command classification is intentionally pragmatic and should keep being refined against real app sessions

## Status

This repo is in the credible CLI MVP stage.

Recent Phase 1 progress:

- `attach` for already-running apps
- `apps --running` process-group picker for already-running apps
- AI coding report sections
- ETW attach-mode process-tree sync
- delete and rename attribution working in elevated mode
- sensitive finding dedupe
- created-file lifecycle normalization in regenerated reports
- `.git` and runtime-noise grouping in reports
- `--watch-all` whole-app live capture
- rename destination synthesis in regenerated reports
- DNS/hostname enrichment in network output
- `Big Picture` and `Activity Buckets` summary layer
- large-session capture controls: `--no-reads`, `--max-events`, `--no-sqlite`
- test project covering normalization, rename synthesis, merge behavior, and summary generation
- grouped network destination/process summaries
- capture settings shown in reports, including disabled file reads for `ai-code`
- include/exclude path filters shown in capture settings and applied before event caps
- app-specific AI profiles for Codex, Claude, Cursor, and VS Code
- process identity fields added to JSON, CSV, SQLite, and HTML report views for file/network attribution
- attribution confidence summary added to the HTML report

## Roadmap

See [ROADMAP.md](ROADMAP.md).
