# AppLedger Plan

Current execution plan after Phase 1 validation.

## Current State

Working now:

- launched-app recording
- attach to running apps
- whole-app live mode with `--watch-all`
- large-session controls with `--no-reads`, `--max-events`, and `--no-sqlite`
- watched-root snapshot diff
- ETW file/process capture when elevated
- sampled network endpoints with hostname enrichment when available
- full-app summary layer with `Big Picture` and `Activity Buckets`
- HTML / JSON / CSV / SQLite outputs
- AI-oriented report sections

Verified against real sessions:

- Codex Desktop
- Claude Desktop
- repo-scoped write tests

## Next Execution Order

### 1. Normalization Tests

Stop collector/report regressions.

Build fixture-driven tests for:

- create then modify
- create then delete
- rename old/new path
- snapshot plus ETW merge
- `.git` suppression
- runtime-noise suppression

### 2. Better Network Grouping

Build:

- group endpoints by hostname and process
- cleaner network summary cards
- prefer domain-style output in report headings

### 3. Smarter AI Session Profiles

Build first-class summaries for:

- Codex
- Claude
- Cursor
- VS Code

### 4. Include / Exclude Path Filters

Build:

- `--include <path>`
- `--exclude <path>`
- multi-use filters for whole-app sessions

## Not Next

These are later, not immediate:

- desktop UI
- broad registry/persistence analysis
- scheduled tasks / services / protocol handlers
- driver-backed collector
- guard / block mode
