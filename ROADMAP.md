# Roadmap

AppLedger is a Windows black box recorder: record an app session, then answer
"what did this app actually do?" with a readable report instead of a raw event
dump. The CLI MVP works today — recording, attach mode, ETW capture, profiles
for AI coding apps, persistence checks, and HTML/JSON/CSV/SQLite output.

## Known rough edges

- Some file-create attribution still relies on normalization rather than
  perfect live ETW create events.
- Hostname correlation for network endpoints is opportunistic, not guaranteed.
- Registry coverage targets high-value persistence locations, not whole-registry
  diffs.
- The Codex profile has been tuned against real sessions; Claude, Cursor, and
  VS Code profiles still need the same pass.
- Long sessions need more real-world validation and test coverage.

## Near term

- Tune the Claude / Cursor / VS Code filter presets against longer real
  recordings.
- Better sensitive-path reporting.
- Cleaner process summary and process-tree presentation.

## Later

- Deeper registry snapshot/diff and richer service/scheduled-task summaries.
- USN journal fallback for missed file changes.
- Process-instance history for exited or PID-reused processes.
- Sharper report structure: better summary cards, timeline, cleanup guidance.
- Signed binaries and additional runtime packages beyond `win-x64`.
- Session comparison and uninstall/leftover analysis.
- Optional background collector service; a lightweight local UI once the
  collector and report formats are stable.

The bar for the report: open `report.html` and understand the session in under
30 seconds.
