# AppLedger

A black box recorder for Windows apps.

AppLedger records what an app does during a session and generates a human-readable report:

- files created, modified, and deleted
- folders and cache growth
- child processes and command lines
- sampled IPv4 TCP connections
- startup registry changes
- risky observations
- conservative cleanup script drafts

```powershell
dotnet run --project src\AppLedger.Cli -- apps code
dotnet run --project src\AppLedger.Cli -- run code --watch "C:\Path\To\Project"
```

Generated files:

- `report.html`
- `session.json`
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

## v0 Scope

This first version is intentionally CLI-first and dependency-light:

- process tree and command capture are live via WMI polling
- network capture is live IPv4 TCP endpoint sampling via `iphlpapi`
- file activity is a before/after snapshot diff of watched roots
- registry capture watches common startup `Run` and `RunOnce` keys

File reads, DNS names, ETW file events, packet contents, blocking, and kernel drivers are not implemented yet.

## Roadmap

See [ROADMAP.md](ROADMAP.md).
