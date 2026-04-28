# AppLedger

A black box recorder for Windows apps.

AppLedger records what an app does during a session and generates a human-readable report:

- files created, modified, and deleted
- live file reads/renames when ETW is available
- folders and cache growth
- child processes and command lines
- sampled IPv4 TCP connections
- startup registry changes
- SQLite session storage
- risky observations
- conservative cleanup script drafts

```powershell
dotnet run --project src\AppLedger.Cli -- apps code
dotnet run --project src\AppLedger.Cli -- run code --watch "C:\Path\To\Project"
```

Generated files:

- `report.html`
- `session.json`
- `session.sqlite`
- `touched-files.csv`
- `commands.json`
- `cleanup.ps1`

## Examples

Record a coding app against a project folder:

```powershell
dotnet run --project src\AppLedger.Cli -- apps cursor
dotnet run --project src\AppLedger.Cli -- run cursor --watch "C:\Users\Anas\Projects\demo-app"
```

Record a short command:

```powershell
dotnet run --project src\AppLedger.Cli -- run "C:\Windows\System32\cmd.exe" --args "/c npm test" --watch "."
```

Take before/after snapshots manually:

```powershell
dotnet run --project src\AppLedger.Cli -- snapshot before.json --watch "C:\Users\Anas\Projects\demo-app"
# run something
dotnet run --project src\AppLedger.Cli -- snapshot after.json --watch "C:\Users\Anas\Projects\demo-app"
dotnet run --project src\AppLedger.Cli -- diff before.json after.json
```

Regenerate reports from a saved session:

```powershell
dotnet run --project src\AppLedger.Cli -- report appledger-runs\20260427-120000\session.sqlite --out regenerated
```

## Phase 1 Scope

This version is still CLI-first, but it now has the first real collector path:

- live ETW process and file capture when AppLedger is run from an elevated terminal
- WMI process polling fallback for command capture
- network capture is live IPv4 TCP endpoint sampling via `iphlpapi`
- file activity falls back to a before/after snapshot diff of watched roots when ETW is unavailable
- registry capture watches common startup `Run` and `RunOnce` keys
- sessions are saved as JSON and SQLite

DNS names, network byte counts, packet contents, blocking, and kernel drivers are not implemented yet.

## Roadmap

See [ROADMAP.md](ROADMAP.md).
