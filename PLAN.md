# AppLedger Plan

Current execution plan after Phase 1 validation.

## Current State

Working now:

- launched-app recording
- attach to running apps
- friendly `record` command for the default flow
- automatic `report.html` opening after recordings
- `--no-open` for scripted runs
- `apps --running` grouped picker for already-running apps
- `--profile ai-code` preset for AI coding sessions
- app-specific profiles for Codex, Claude, Cursor, and VS Code
- automatic profile inference in `record`
- multi-use `--include` / `--exclude` path filters for live events and snapshots
- process-name recording resolves to the root matching process tree
- process-name recording deprioritizes AppLedger/dotnet command-line self matches
- whole-app live mode with `--watch-all`
- large-session controls with `--no-reads`, `--max-events`, and `--no-sqlite`
- watched-root snapshot diff
- ETW file/process capture when elevated
- sampled network endpoints with hostname enrichment when available
- grouped network destination/process summaries
- full-app summary layer with `Big Picture` and `Activity Buckets`
- first-screen HTML report summary with risk state, priority observations, and compact session cards
- watched-root changes labeled separately from source/project intent
- known-byte wording for ETW sessions where size deltas are unavailable
- report-visible capture settings, including disabled file-read capture
- report-visible include/exclude filters
- process identity snapshots on file/network events for stronger attribution
- attribution confidence and reason in JSON, CSV, SQLite, and HTML reports
- higher-signal risk observations for AI coding sessions
- broader persistence risk observations for startup registry, Startup folders, services, scheduled tasks, protocol handlers, and file associations
- conservative cleanup guidance in HTML and `cleanup.ps1`
- active process-tree membership guarded against PID reuse before ETW/network sync
- HTML / JSON / CSV / SQLite outputs
- AI-oriented report sections
- test project with passing normalization and summary fixtures
- source split into focused CLI, collection, filesystem, analysis, model, output, registry, and report files

Verified against real sessions:

- Codex Desktop
- Claude Desktop
- repo-scoped write tests

## Next Execution Order

### 1. Tune AI Session Profiles

Tune first-class profile behavior for:

- Codex
- Claude
- Cursor
- VS Code

Keep this refactor-friendly: if the analyzer grows while tuning app-specific behavior, split `Analysis/Analyzers.cs` into profile-specific files instead of letting it become the new monolith.

Use the hardened process tree as the baseline: profile tuning should not reintroduce broad PID-only matching, and suspicious unrelated processes in reports should be treated as attribution bugs until proven otherwise.

The generic entry point is now:

```powershell
appledger record codex --watch .
appledger record claude --watch .
appledger record cursor --watch .
appledger record code --watch .
appledger apps --running codex
appledger attach codex --profile ai-code
```

### 2. Persistence Summary Polish

Built:

- broader startup/persistence checks
- Startup folder, scheduled task, service, protocol handler, and file association detection
- cache/temp cleanup grouping
- conservative cleanup script annotations

Next:

- dedicated persistence section in HTML
- clean-state wording such as "added no startup persistence"
- scheduled task command/action extraction where available
- service image-path and start-mode summaries

## Not Next

These are later, not immediate:

- desktop UI
- whole-registry diffing
- enforcement/blocking
- driver-backed collector
- guard / block mode
