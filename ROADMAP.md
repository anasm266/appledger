# AppLedger Roadmap

AppLedger should become a Windows black box recorder that explains what an app did during a session: files, folders, registry, commands, network, cache, and cleanup.

The product should not feel like a ProcMon clone. ProcMon shows events. AppLedger should explain a session.

## What Works Now

The current v0 is a CLI prototype.

It can:

- launch an app under recording
- resolve easy app names like `code`, `cursor`, `chrome`, and `notepad`
- search recordable apps with `appledger apps [search]`
- track the launched process and longer-lived child processes through WMI polling
- capture command lines for observed processes
- sample IPv4 TCP network endpoints by process ID
- snapshot watched folders before and after a session
- detect files created, modified, and deleted in watched folders
- classify paths as app data, temp, documents, downloads, sensitive, project/user files, and similar buckets
- detect common startup registry key changes
- generate `report.html`, `session.json`, `touched-files.csv`, `commands.json`, and `cleanup.ps1`

Example:

```powershell
dotnet run --project src\AppLedger.Cli -- apps code
dotnet run --project src\AppLedger.Cli -- run code --watch "C:\Users\Anas\Projects\demo-app"
```

Manual diff mode also works:

```powershell
dotnet run --project src\AppLedger.Cli -- snapshot before.json --watch "."
dotnet run --project src\AppLedger.Cli -- snapshot after.json --watch "."
dotnet run --project src\AppLedger.Cli -- diff before.json after.json
```

## Current Limits

The main limitation is fidelity. File changes are detected with before/after snapshots, not live ETW file events.

The current version does not yet capture:

- file reads
- exact live file event timeline
- file renames as first-class events
- DNS/domain names
- network byte counts
- packet contents
- broad registry diffs
- scheduled tasks
- services
- protocol handlers
- blocking or guard mode
- desktop UI
- SQLite storage
- minifilter driver-level data

## Phase 1: Credible CLI MVP

Goal: make AppLedger genuinely useful for AI coding app sessions.

Build:

- ETW-backed process creation capture
- ETW-backed file create, modify, rename, and delete events
- reliable process-tree attribution using PID plus process start time
- command timeline
- SQLite session database
- richer JSON schema
- report regeneration from saved sessions
- focused AI coding report sections:
  - changed project files
  - commands run
  - package installs
  - tests run
  - git commands
  - sensitive locations touched

Target demo:

```txt
I recorded Cursor editing a repo.
AppLedger shows files changed, commands run, package installs, network endpoints, and risky activity.
```

## Phase 2: Strong Windows Observability

Goal: make the collector technically serious.

Build:

- DNS event correlation so reports show domains, not just IPs
- registry snapshot/diff for high-value locations
- startup and persistence detection:
  - Run keys
  - scheduled tasks
  - services
  - protocol handlers
  - file associations
- USN journal fallback for missed file changes
- top folder growth by byte delta
- improved cleanup planner
- elevated mode detection
- event confidence labels

Target demo:

```txt
This app added startup persistence, wrote 600 MB of cache, spawned PowerShell, and connected to these domains.
```

## Phase 3: Star-Worthy Report

Goal: make the report the feature people remember.

Build:

- polished HTML report
- session summary cards
- risk score
- top changed folders
- timeline view
- process tree view
- developer actions section
- privacy-sensitive access section
- cleanup section with conservative guidance
- `appledger report <session>` command
- single-file executable release

The report should open with the answer, not the raw log:

```txt
Big picture:
- Edited 17 project files
- Ran 12 commands
- Installed 2 packages
- Read .env
- Connected to api.openai.com, github.com, registry.npmjs.org
- Added no startup items
```

## Phase 4: Desktop App

Goal: make AppLedger usable without a terminal.

Build:

- Tauri, WPF, or WinUI frontend
- app picker
- project/folder picker
- start/stop recording
- live session timer
- recent sessions
- report viewer
- export buttons
- cleanup draft viewer

Target workflow:

```txt
Choose app -> choose folder -> Start Recording -> Stop -> See report
```

## Phase 5: Advanced Differentiators

Goal: make AppLedger more than a readable log viewer.

Build:

- AI Agent Activity mode
- before/after uninstall report
- extension install detection
- baseline comparison between sessions
- optional elevated collector service
- signed binary
- optional persistent background collector
- optional minifilter driver for highest-fidelity file monitoring
- later guard mode:
  - warn on sensitive file reads
  - warn on shell execution
  - warn on persistence changes

Guard mode should come later. v0 should observe only.

## End State

By the end, AppLedger should answer:

```txt
What did this Windows app actually do to my computer?
```

It should show:

- files read, created, modified, renamed, and deleted
- folders and cache growth
- child processes and exact commands
- registry changes
- startup and persistence changes
- scheduled tasks, services, protocol handlers, and file associations
- network domains, IPs, and ports
- sensitive file access
- cleanup opportunities
- human-readable risk observations

The strongest final demo is an AI coding session:

```txt
Record Cursor or Codex Desktop editing a repo.

AppLedger shows:
- 17 project files modified
- 4 files created
- 1 package installed
- 12 commands run
- .env read
- PowerShell spawned
- registry unchanged
- network calls to api.openai.com, github.com, registry.npmjs.org
- cleanup available: 240 MB cache
```

