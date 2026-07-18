# ProcLens

ProcLens is a local-first Windows process-history application that explains
where memory went, what launched, and which coding or desktop session owns each
process tree. It runs quietly in the Windows notification area and serves a
professional dashboard on loopback only.

## What it shows

- Physical-memory and commit pressure over time.
- Application and session-level private memory, CPU, I/O, handles, and threads.
- Process starts and stops across collector runs and reboots.
- Ownership tracing for terminals, browsers, Claude, Codex, and local tools.
- A review queue for processes whose owner could not be resolved.

ProcLens is not a file/registry tracer and does not replace Sysinternals Process
Monitor. It focuses on low-overhead historical resource and process genealogy.

## Privacy and security

ProcLens sends no telemetry and binds only to `127.0.0.1`. Its API validates the
local Host header and requires a random per-install token. Command lines,
executable paths, machine names, user names, and environment variables are not
stored by default. See [PRIVACY.md](PRIVACY.md) for the complete data policy.

History uses an indexed SQLite database in
`%LOCALAPPDATA%\ProcLens\data\proclens.db` and is retained for 14 days by
default. Existing ProcWatch JSONL history is imported once with paths, command
lines, machine names, and user names removed; the original files are untouched.

## Run from source

Requirements: Windows 10/11 and the .NET 10 SDK.

```powershell
dotnet run -c Release
```

The app appears in the notification area without opening a terminal. Double
click its icon to open the dashboard. The tray menu can pause collection, open
the data folder, show diagnostics, enable startup, or exit.

## Build a release

```powershell
dotnet publish ProcLens.csproj -c Release -r win-x64 --self-contained true -o artifacts\publish\win-x64
```

Run `ProcLens.exe install` from that published folder to copy the release to
`%LOCALAPPDATA%\Programs\ProcLens`, enable per-user startup, and launch the tray
app. `ProcLens.exe uninstall` disables startup and preserves local history.

## Diagnostic commands

```text
ProcLens.exe tray [--background]
ProcLens.exe dashboard
ProcLens.exe doctor
```

Advanced opt-ins:

```text
--capture-command-lines
--capture-paths
--retention-days 30
--interval 30
--scan 5
--data-dir PATH
```

Persistent settings live at `%LOCALAPPDATA%\ProcLens\settings.json`. Opt-in
flags affect only the current invocation unless the equivalent property is
changed in that settings file.

## Classification rules

Generic application rules are defined in `rules.default.json`; the collector
contains no ViriCrew-, project-, or vendor-specific classifier code. To add or
override classifications, create `%LOCALAPPDATA%\ProcLens\rules.json`. Custom
rules are evaluated before the generic defaults and take effect after restart.

`rules.viricrew.example.json` demonstrates process-name, command substring,
regular-expression, and owner-root rules. Copy it to the path above only if you
want those integrations. Rule files are local configuration and must never
contain secrets or full private command lines.

## Development

```powershell
dotnet build ProcLens.csproj -c Release -p:TreatWarningsAsErrors=true
dotnet test tests\ProcLens.Tests\ProcLens.Tests.csproj -c Release
```

ProcLens is licensed under the [MIT License](LICENSE). Contributions should
preserve local-only operation and privacy-safe defaults.
