# ProcLens advisory contracts (v1)

Use only the documented commands below. JSON is written to stdout; diagnostics
are written to stderr.

```text
ProcLens.exe agent-snapshot --minutes N [--data-dir PATH]
ProcLens.exe recommendations list [--data-dir PATH]
ProcLens.exe recommendations import --file PATH [--minutes N] [--data-dir PATH]
```

`N` is 1 through 1440. Import accepts an advisory document of at most 1 MiB.
It is an advisory operation, never a process action.

## Snapshot

The privacy-safe snapshot has `schemaVersion: 1`, `snapshotHash`,
`generatedAtUtc`, `window`, `freshness`, `coverage`, `groups`, and existing
core `recommendations`. It contains no command lines, executable paths, user
or machine names, or environment variables.

Each group includes `groupKey`, `label`, `root` (`pid`, `startTicks`), complete
`members`, `ownerResolved`, `metrics` (`privateMemoryMb`,
`sustainedCpuPct`), `lastActivityAtUtc`, and `safety`. A member also reports
process/activity/sample fields and privacy-safe safety signals. `safety` has
`isHardBlocked`, categorical `risk`, and `flags`.

Refuse to advise when `freshness.isFresh` is false, when a target lacks fresh
member samples, or when a group is unresolved or hard-blocked. Do not turn a
high memory/CPU total into a higher confidence value.

## Advisory document

Use this exact shape; unknown fields are invalid.

```json
{
  "schemaVersion": 1,
  "advisoryId": "analysis-20260718-01",
  "snapshotHash": "64-lowercase-hex-characters",
  "snapshotGeneratedAtUtc": "2026-07-18T12:00:00Z",
  "createdAtUtc": "2026-07-18T12:01:00Z",
  "expiresAtUtc": "2026-07-18T12:30:00Z",
  "recommendations": [
    {
      "root": { "pid": 1234, "startTicks": 638000000000000000 },
      "members": [{ "pid": 1234, "startTicks": 638000000000000000 }],
      "action": "investigate",
      "confidencePct": 55,
      "evidence": [
        { "code": "history.idle", "detail": "The complete group has no recent activity in this snapshot." }
      ]
    }
  ]
}
```

Rules enforced by the bundled validator and ProcLens:

- Use schema version 1, an `advisoryId` of 1–80 `[A-Za-z0-9._-]` characters,
  a 64-character lowercase SHA-256 snapshot hash, and 1–100 recommendations.
- Use the snapshot's hash, generated time, root, and exact complete member set.
  Never invent or repair an identity. `pid` plus `startTicks` is the identity.
- Make the advisory and snapshot no more than five minutes old; do not
  future-date them. Expire after creation and within one hour.
- Submit `investigate` only. `closeGracefully`, `restart`, and `disableStartup`
  are never valid agent actions.
- Keep submitted confidence within 0–70. ProcLens independently recomputes
  confidence and applies its own lower ceilings for stale, undersampled, or
  unresolved evidence. It never lets agent input raise the result.
- Supply 1–16 evidence items per recommendation. Each `code` is 1–64 safe
  characters; each non-empty `detail` is at most 500 characters, contains no
  control characters, paths, credentials, bearer tokens, or environment-like
  assignments.

## Safety blocks

Never submit a target carrying a hard block. ProcLens blocks itself, Explorer,
protected/system targets, system-like, session-0, and service-like processes;
unresolved groups; foreground groups; recently started processes; invalid or
changed identities; and rules marked `neverEnd`. Import repeats these checks on
the live process state, so a previously valid document can become invalid.

## Queue semantics

`recommendations list` returns a version-1 document with `generatedAtUtc` and
recommendation records. A record keeps provenance (`core` or `agent` and an
optional advisory id), confidence (`pct` and low/medium/high kind), categorical
risk, expected impact, evidence, timestamps, and state. Confidence describes
evidence of current unnecessary use; impact estimates potential reclaimed
private memory and sustained CPU; risk describes the action safety gate.
