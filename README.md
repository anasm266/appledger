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
- capture live file and process events with ETW when run elevated
- fall back to before/after watched-folder snapshots when ETW is unavailable
- sample IPv4 TCP endpoints by process ID
- capture command lines from the observed process tree
- detect common startup `Run` and `RunOnce` registry changes
- persist sessions as JSON and SQLite
- regenerate reports from `session.json` or `session.sqlite`
- generate AI-session sections for project file changes, developer commands, sensitive access, and process timeline

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

Record an app you launch:

```powershell
dotnet run --project src\AppLedger.Cli -- run code `
  --watch "C:\Users\Anas\Projects\demo-app" `
  --out artifacts\vscode-session `
  --timeout 300
```

Attach to an app that is already running:

```powershell
dotnet run --project src\AppLedger.Cli -- ps codex

dotnet run --project src\AppLedger.Cli -- attach 20376 `
  --watch "C:\Users\Anas\Documents\New project 8" `
  --out artifacts\codex-self `
  --timeout 300
```

Regenerate a report from a saved session:

```powershell
dotnet run --project src\AppLedger.Cli -- report artifacts\codex-self\session.sqlite `
  --out artifacts\codex-self-regenerated
```

Take manual snapshots and diff them:

```powershell
dotnet run --project src\AppLedger.Cli -- snapshot before.json --watch "."
# run something
dotnet run --project src\AppLedger.Cli -- snapshot after.json --watch "."
dotnet run --project src\AppLedger.Cli -- diff before.json after.json
```

## CLI

```text
appledger apps [search]
appledger ps [search]
appledger run <app name|alias|exe> [--args "<arguments>"] [--watch <path>] [--out <dir>] [--timeout <seconds>]
appledger attach <pid|process search> [--watch <path>] [--out <dir>] [--timeout <seconds>]
appledger report <session.json|session.sqlite> [--out <dir>]
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
dotnet run --project src\AppLedger.Cli -- attach 20376 `
  --watch "C:\Users\Anas\Documents\New project 8" `
  --out artifacts\codex-self `
  --timeout 300
```

3. Use the app normally, then open `report.html`.

What the report can now surface for AI sessions:

- changed project files
- shell and git commands
- sensitive file access such as `.env`
- rename and delete activity
- sampled outbound endpoints
- grouped process activity

## Known Limits

What is not done yet:

- DNS/domain correlation for network endpoints
- network byte counts
- packet contents
- broad registry diffs beyond current startup keys
- scheduled tasks, services, protocol handlers, and file associations
- GUI desktop app
- guard/block mode
- kernel driver or minifilter collector

What still needs refinement:

- rename destination synthesis is still partial, so rename targets can still appear as snapshot-created files
- some file-create attribution still depends on normalization rather than a perfect live ETW create event for every path
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

## Roadmap

See [ROADMAP.md](ROADMAP.md).
