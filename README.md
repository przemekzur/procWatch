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

## Optimization queue and safe actions

ProcLens can surface an optimization queue for complete application/process
groups whose history suggests meaningful private-memory or sustained-CPU use.
The queue is a prompt to review, not an automatic optimizer. Each item shows
its source (core analysis or a CLI agent), evidence, confidence, action risk,
and expected private-memory and sustained-CPU impact.

Confidence is the evidence that a group is currently unnecessary; impact is an
estimate of resources that could be recovered; risk is a separate categorical
safety gate. A large memory number does not raise confidence, and an agent
cannot raise ProcLens confidence above the policy ceiling. Agent-only advice is
capped at 70% until ProcLens core evidence corroborates it.

The dashboard lets an authenticated local user mark a recommendation Needed,
Snooze it, or explicitly choose **Close gracefully** when that action is shown.
There is no automatic termination and no force-termination feature. Before a
graceful close, ProcLens revalidates the process identity (PID plus start time)
and all current safety blocks. Protected, system, service-like, session-0,
foreground, recently started, unresolved, changed-identity, `neverEnd`, and
ProcLens targets are blocked.

## Agent advisory CLI

CLI agents are advisory-only and can never control a process. ProcLens exposes
three versioned JSON commands (JSON on stdout, diagnostics on stderr):

```powershell
ProcLens.exe agent-snapshot --minutes 30 > snapshot.json
ProcLens.exe recommendations list > recommendations.json
ProcLens.exe recommendations import --file advisory.json --minutes 30
```

The snapshot is privacy-safe and includes group identities, history-derived
metrics, activity, freshness, coverage, and safety flags—never command lines,
paths, user/machine names, or environment values. Import accepts only fresh,
version-1 advisory documents that match the current snapshot and complete group
membership. It recomputes safety, identity, impact, and confidence, and accepts
only the non-executing `investigate` action.

To install the bundled Codex skill locally, copy
`skills\proclens-process-advisor` into `%USERPROFILE%\.codex\skills\` and restart
the CLI session. Invoke it for a slow-PC investigation or a ProcLens snapshot;
it validates an advisory before import and refuses unsafe, stale, malformed, or
overconfident submissions.

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
