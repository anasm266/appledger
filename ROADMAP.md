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
- `--profile ai-code` to choose sensible AI coding session defaults
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
- process-name recording now prefers the root of a matching process tree instead of an arbitrary child process
- running-app selection now groups Electron/helper processes under the best app root and deprioritizes AppLedger's own command line
- startup `Run` / `RunOnce` registry monitoring
- HTML, JSON, CSV, SQLite, AI activity, and cleanup outputs
- first-screen whole-app summary via `Big Picture` and `Activity Buckets`
- capture settings displayed in reports so disabled categories are clear
- process identity snapshots attached to file and network events for attribution
- attribution confidence and reason attached to file/network events
- large-session controls: `--no-reads`, `--max-events`, `--no-sqlite`
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
- process identity fields added across JSON, CSV, SQLite, and HTML report output
- attribution quality summary added to reports
- running app picker added for friendlier `record codex --watch .` style workflows

Current proof point:

```txt
Record Codex Desktop, Claude Desktop, or another Electron app.
AppLedger shows app-data churn, project file changes when a watch root is provided,
shell and git commands, sensitive file access, rename/delete activity, sampled endpoints,
and grouped process activity.
```

## Current Gaps

Still rough or incomplete:

- some file create attribution still relies on normalization instead of perfect live ETW creates
- PID reuse is not fully solved yet; event attribution now carries PID plus process identity fields and confidence levels, but live collector membership should move further toward process-instance keys
- hostname correlation is opportunistic, not guaranteed for every endpoint
- command parsing is pragmatic, not exhaustive
- registry coverage is narrow
- include/exclude path filtering is still missing
- only one profile exists today: `ai-code`
- large sessions still need test coverage and tuning
- no desktop UI yet
- `Analysis/Analyzers.cs` is still the largest file and can be split further once AI profiles mature

These are product polish and fidelity gaps, not viability gaps. The tool is already demonstrable.

## Next Up

Immediate next work should stay focused on making AI desktop app sessions more opinionated and easier to scan.

### 1. Smarter AI Session Profiles

Goal: make Codex/Cursor/VS Code/Claude sessions the strongest demo.

Build:

- app-specific profiles beyond the generic `ai-code` profile
- better grouping of project files vs cache/temp/internal repo files
- command grouping by high-level action
- improved sensitive path reporting
- cleaner process summary / process tree presentation

Success looks like:

```txt
Changed project files: 6
Commands run: 9
Sensitive paths touched: .env
Shells spawned: PowerShell
```

### 2. Include / Exclude Path Filtering

Goal: let whole-app mode stay broad without being noisy.

Build:

- `--include <path>`
- `--exclude <path>`
- likely multiple-use flags

### 3. Better Risk Observations

Goal: make the top of the report feel opinionated.

Build:

- persistence-oriented registry detections beyond current keys
- startup/persistence summary section
- higher-signal sensitive access findings
- shell spawn and package install findings tuned for AI sessions
- cache growth / temp growth thresholds

Success looks like the first screen answering:

```txt
Big picture:
- touched 8 project files
- read .env
- spawned PowerShell
- ran 6 git commands
- added no startup persistence
```

## Phase 2

Goal: strong Windows observability beyond the MVP.

Build:

- broader registry snapshot/diff for high-value locations
- scheduled task detection
- service detection
- protocol handler and file association detection
- USN journal fallback for missed file changes
- more reliable process attribution with PID plus start-time semantics

This is where AppLedger stops being "useful CLI prototype" and becomes a technically serious Windows introspection tool.

## Phase 3

Goal: make the report the star magnet and the CLI comfortable to use.

Build:

- sharper HTML report structure
- better top-level summary cards
- stronger process-tree and timeline sections
- more opinionated cleanup guidance
- better export ergonomics
- release-ready single binary packaging
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
- shell spawns
- .env access
- network destinations
- startup registry unchanged
- cleanup available
```
