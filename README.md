# AppLedger

A black box recorder for Windows apps.

AppLedger records what an app did during a session and turns it into a readable report: files touched, child processes, command lines, sampled network endpoints, sensitive path access, and cleanup hints.

The product angle is not "ProcMon clone." ProcMon shows events. AppLedger explains a session.

## What Works Now

Current Phase 1 capabilities:

- record a launched app with `run`
- attach to an already-running app with `attach`
- discover easy app aliases with `apps`
- search running processes with `ps`
- record whole-app live file activity with `--watch-all`
- capture live file and process events with ETW when run elevated
- fall back to before/after watched-folder snapshots when ETW is unavailable
- sample IPv4 TCP endpoints by process ID
- enrich network endpoints with cached DNS / reverse lookup hostnames when possible
- group network activity by destination and process
- capture command lines from the observed process tree
- detect common startup `Run` and `RunOnce` registry changes
- summarize whole-app sessions with a top-level "Big Picture" and activity buckets
- persist sessions as JSON and SQLite
- regenerate reports from `session.json` or `session.sqlite`
- generate AI-session sections for project file changes, developer commands, sensitive access, and process timeline
- normalize rename targets in regenerated reports and display renames as `old -> new`
- group `.git` internals and runtime bookkeeping out of the main report tables
- control large sessions with `--no-reads`, `--max-events <n>`, and `--no-sqlite`
- cover normalization and summary logic with unit tests

Generated artifacts:

- `report.html`
- `session.json`
- `session.sqlite`
- `touched-files.csv`
- `commands.json`
- `ai-activity.json`
- `cleanup.ps1`

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

- `--watch-all` for "what did this app touch anywhere"
- `--watch <path>` for "what changed in this repo/folder"
- both together if you want whole-app activity plus a cleaner project diff

The best current control flags for noisy sessions are:

- `--no-reads` to drop the highest-volume ETW category
- `--max-events <n>` to stop once the session reaches a live event cap
- `--no-sqlite` to skip `session.sqlite` when you only want HTML/JSON/CSV

## Quick Start

Prerequisites:

- Windows
- .NET 8 SDK
- elevated PowerShell if you want live ETW file/process capture

Find an app by alias or search:

```powershell
dotnet run --project src\AppLedger.Cli -- apps code
dotnet run --project src\AppLedger.Cli -- apps cursor
```

For day-to-day testing, prefer the published binary:

```powershell
dotnet publish src\AppLedger.Cli\AppLedger.Cli.csproj -c Release -o artifacts\publish-test
```

Record an app you launch:

```powershell
.\artifacts\publish-test\appledger.exe run code `
  --watch "C:\Users\Anas\Projects\demo-app" `
  --out artifacts\vscode-session `
  --timeout 300
```

Attach to an app that is already running:

```powershell
dotnet run --project src\AppLedger.Cli -- ps codex

.\artifacts\publish-test\appledger.exe attach 40396 `
  --watch "C:\Users\Anas\Documents\New project 8" `
  --out artifacts\codex-self `
  --timeout 300
```

Record the full app, not just one watched root:

```powershell
.\artifacts\publish-test\appledger.exe attach 40396 `
  --watch-all `
  --no-reads `
  --max-events 50000 `
  --out artifacts\codex-full `
  --timeout 300
```

Record the full app and also keep a repo snapshot diff:

```powershell
.\artifacts\publish-test\appledger.exe attach 40396 `
  --watch-all `
  --watch "C:\Users\Anas\Documents\New project 8" `
  --no-reads `
  --max-events 50000 `
  --out artifacts\codex-full `
  --timeout 300
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
appledger ps [search]
appledger run <app name|alias|exe> [--args "<arguments>"] [--watch <path>] [--watch-all] [--no-reads] [--max-events <n>] [--no-sqlite] [--out <dir>] [--timeout <seconds>]
appledger attach <pid|process search> [--watch <path>] [--watch-all] [--no-reads] [--max-events <n>] [--no-sqlite] [--out <dir>] [--timeout <seconds>]
appledger report <session.json|session.sqlite> [--out <dir>] [--no-sqlite]
appledger snapshot <output.json> --watch <path>
appledger diff <before.json> <after.json>
```

Useful examples:

```powershell
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
```

2. Attach to the root PID from an elevated terminal:

```powershell
.\artifacts\publish-test\appledger.exe attach 40396 `
  --watch-all `
  --watch "C:\Users\Anas\Documents\New project 8" `
  --no-reads `
  --max-events 50000 `
  --out artifacts\codex-full `
  --timeout 300
```

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

## Roadmap

See [ROADMAP.md](ROADMAP.md).
