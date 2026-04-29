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
- process-name recording now prefers the root of a matching process tree instead of an arbitrary child process
- running-app selection now groups Electron/helper processes under the best app root and deprioritizes AppLedger's own command line
- startup `Run` / `RunOnce` registry monitoring
- higher-signal risk observations for sensitive paths, shell spawns, package installs, network tools, external endpoints, and outside-root writes
- HTML, JSON, CSV, SQLite, AI activity, and cleanup outputs
- first-screen whole-app summary via `Big Picture` and `Activity Buckets`
- polished first-screen HTML summary with risk state, priority observations, and compact session cards
- capture settings displayed in reports so disabled categories are clear
- watched-root changes are labeled separately from source/project intent
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
- running app picker added for friendlier `record codex --watch .` style workflows
- app-specific AI profiles added on top of the generic `ai-code` defaults
- automatic report opening added with `--no-open` escape hatch
- report wording tightened for watched-root changes and known-byte totals

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
- process attribution is much stronger than the initial PID-only version, but long-session edge cases still need more real-world validation
- hostname correlation is opportunistic, not guaranteed for every endpoint
- command parsing is pragmatic, not exhaustive
- registry coverage is narrow
- app-specific filter presets exist, but they still need tuning against longer real Codex, Claude, Cursor, and VS Code sessions
- large sessions still need test coverage and tuning
- no desktop UI yet
- `Analysis/Analyzers.cs` is still the largest file and can be split further once AI profiles mature

These are product polish and fidelity gaps, not viability gaps. The tool is already demonstrable.

## Next Up

Immediate next work should stay focused on making AI desktop app sessions more opinionated and easier to scan.

### 1. Tune AI Session Profiles Against Real Runs

Goal: make Codex/Cursor/VS Code/Claude sessions the strongest demo.

Build:

- tune Codex/Claude/Cursor/VS Code filter presets from real reports
- better grouping of source/project files vs watched-root temp files, cache/temp, and internal repo files
- command grouping by high-level action
- improved sensitive path reporting
- cleaner process summary / process tree presentation

Success looks like:

```txt
Watched-root paths changed: 6
Commands run: 9
Sensitive paths touched: .env
Shells spawned: PowerShell
```

### 2. Persistence and Cleanup Polish

Goal: make the top of the report explain whether an app tried to stick around or left obvious cleanup behind.

Build:

- persistence-oriented registry detections beyond current `Run` / `RunOnce` keys
- startup folder, scheduled task, service, protocol handler, and file association checks
- startup/persistence summary section
- safer cache/temp cleanup classification
- clearer `cleanup.ps1` review comments

Success looks like the first screen answering:

```txt
Big picture:
- touched 8 watched-root paths
- read .env
- spawned PowerShell
- ran 6 git commands
- added no startup persistence
- left 800 MB likely cache
```

## Phase 2

Goal: strong Windows observability beyond the MVP.

Build:

- broader registry snapshot/diff for high-value locations
- scheduled task detection
- service detection
- protocol handler and file association detection
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
