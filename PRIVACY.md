# Privacy

ProcLens is local-first. It binds its dashboard to `127.0.0.1`, sends no
telemetry, and performs no cloud requests.

By default ProcLens stores process names, categories, ownership labels,
resource metrics, process identifiers, start/stop events, collector run IDs,
and boot timestamps. It does **not** store the computer name, Windows user name,
executable paths, environment variables, or full command lines.

Executable-path and command-line collection are separate, explicit opt-ins in
`%LOCALAPPDATA%\ProcLens\settings.json` or through the diagnostic command-line
flags. Secret-like arguments are redacted and the user-profile directory is
replaced with `%USERPROFILE%`, but no redactor is perfect. Do not enable these
options unless you need them.

History is stored in `%LOCALAPPDATA%\ProcLens\data\proclens.db` and automatically
deleted after the configured retention period (14 days by default). Removing
that directory permanently removes the history.

The dashboard API requires a random local token held in `settings.json`. Treat
that settings file as private user data.
