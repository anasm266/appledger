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
- `ps` to search running processes
- `run` to launch and record an app
- `attach` to record an already-running process tree
- `report` to regenerate artifacts from `session.json` or `session.sqlite`
- `snapshot` and `diff` for manual before/after workflows
- ETW live process and file capture when run elevated
- WMI process-tree polling fallback
- sampled IPv4 TCP endpoint capture
- startup `Run` / `RunOnce` registry monitoring
- HTML, JSON, CSV, SQLite, AI activity, and cleanup outputs

Recent Phase 1 fixes:

- attach-mode ETW process-tree sync for already-running apps
- delete and rename attribution working in elevated mode
- sensitive finding dedupe
- created-file lifecycle normalization for report/session output
- cleaner AI coding summaries with lower command/process noise

Current proof point:

```txt
Record Codex Desktop or Cursor against a watched repo.
AppLedger shows project file changes, shell and git commands, sensitive file access,
rename/delete activity, sampled endpoints, and grouped process activity.
```

## Current Gaps

Still rough or incomplete:

- rename destination synthesis is partial
- some file create attribution still relies on normalization instead of perfect live ETW creates
- network output is IP/port only, not DNS/domain aware
- command parsing is pragmatic, not exhaustive
- registry coverage is narrow
- no desktop UI yet

These are product polish and fidelity gaps, not viability gaps. The tool is already demonstrable.

## Next Up

Immediate next work should stay focused on making the report stronger for AI coding sessions.

### 1. File Event Fidelity

Goal: make file lifecycle reporting feel exact.

Build:

- synthesize rename destination-side outcomes
- reduce duplicate ETW read noise further
- improve create vs modify attribution for short-lived files
- add confidence labels where attribution is reconstructed

Success looks like:

```txt
Created: 3
Modified: 1
Deleted: 1
Renamed: 1
```

with those numbers matching what a user believes happened.

### 2. Smarter AI Session Report

Goal: make Codex/Cursor/VS Code sessions the strongest demo.

Build:

- better grouping of project files vs cache/temp/internal repo files
- command grouping by high-level action:
  - git
  - test
  - package install
  - shell
  - script
- better suppression of helper-process noise
- improved sensitive path reporting
- cleaner process summary / process tree presentation

Success looks like:

```txt
Changed project files: 6
Commands run: 9
Sensitive paths touched: .env
Shells spawned: PowerShell
```

instead of a long process dump.

### 3. Network Context

Goal: move from IPs to understandable destinations.

Build:

- DNS correlation where possible
- endpoint grouping by process
- better "big picture" network summary in HTML report

Success looks like:

```txt
Connected to:
- github.com
- registry.npmjs.org
- api.openai.com
```

instead of only raw IP addresses.

### 4. Better Risk Observations

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

Goal: make the report the star magnet.

Build:

- sharper HTML report structure
- better top-level summary cards
- stronger process-tree and timeline sections
- more opinionated cleanup guidance
- better export ergonomics
- release-ready single binary packaging

The rule for this phase:

```txt
Open report.html and understand the session in under 30 seconds.
```

## Phase 4

Goal: desktop UI.

Build:

- app picker
- watch-folder picker
- start / stop recording
- recent sessions
- report viewer
- export buttons

This should come after the collector and report are stable enough that a UI is not hiding unstable behavior.

## Phase 5

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
