# AppLedger Plan

Current execution plan after Phase 1 validation.

## Current State

Working now:

- launched-app recording
- attach to running apps
- whole-app live mode with `--watch-all`
- watched-root snapshot diff
- ETW file/process capture when elevated
- sampled network endpoints with hostname enrichment when available
- HTML / JSON / CSV / SQLite outputs
- AI-oriented report sections

Verified against real sessions:

- Codex Desktop
- Claude Desktop
- repo-scoped write tests

## Next Execution Order

### 1. Full-App Summary Layer

Make `--watch-all` readable without opening raw tables first.

Build:

- top-level buckets for:
  - app data / cache
  - temp churn
  - project files
  - git metadata
  - system/runtime noise
  - sensitive paths
  - network destinations
- clearer first-screen summary text

Success:

```txt
Codex mostly updated temp/cache state, read .gitconfig, ran git commands,
and contacted GitHub.
```

### 2. Large-Session Controls

Keep whole-app mode practical.

Build:

- `--no-reads`
- `--no-sqlite`
- `--max-events <n>`
- optional include/exclude path filters

### 3. Normalization Tests

Stop collector/report regressions.

Build fixture-driven tests for:

- create then modify
- create then delete
- rename old/new path
- snapshot plus ETW merge
- `.git` suppression
- runtime-noise suppression

### 4. Better Network Grouping

Build:

- group endpoints by hostname and process
- cleaner network summary cards
- prefer domain-style output in report headings

### 5. Smarter AI Session Profiles

Build first-class summaries for:

- Codex
- Claude
- Cursor
- VS Code

## Not Next

These are later, not immediate:

- desktop UI
- broad registry/persistence analysis
- scheduled tasks / services / protocol handlers
- driver-backed collector
- guard / block mode
