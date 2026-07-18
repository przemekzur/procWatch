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

## CLI agent advisories

ProcLens does not send snapshots or advisories to a service. `agent-snapshot`
writes a local, versioned JSON document to the caller's stdout. Its group and
member fields are limited to process identity, process name, resource/activity
history, sampling coverage, and safety/provenance data. It deliberately omits
command lines, executable paths, machine names, Windows user names, environment
variables, and user documents.

A CLI agent may create a local advisory file and ask ProcLens to import it.
The advisory contains only a snapshot hash/time, complete process identities,
an `investigate` recommendation, bounded confidence, and short privacy-safe
evidence. ProcLens rejects path-like, credential-like, or environment-like
evidence. The agent has no API for process control and imported advice is
labelled with agent provenance in the local queue.
