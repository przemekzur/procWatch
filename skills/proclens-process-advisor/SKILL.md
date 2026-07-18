---
name: proclens-process-advisor
description: Analyze a slow Windows PC with ProcLens by interpreting privacy-safe ProcLens history, identifying stale or resource-heavy process groups, and publishing non-executing recommendations to the ProcLens dashboard. Use when a CLI agent is asked to inspect `ProcLens agent-snapshot` output, explain a ProcLens optimization queue, advise on an apparently idle application group, or prepare and import a safe ProcLens advisory document.
---

# ProcLens Process Advisor

Produce advice only. ProcLens owns collection, grouping, safety, confidence,
persistence, and every process action. Never terminate, kill, suspend, restart,
or otherwise control a process. Never use an arbitrary PID: address a complete
group by its root identity and every member identity.

## Workflow

1. Capture a new snapshot; keep diagnostics separate from JSON.

   ```powershell
   & ProcLens.exe agent-snapshot --minutes 30 > snapshot.json
   ```

2. Read `references/contracts.md`. Check that `schemaVersion` is `1`,
   `freshness.isFresh` is true, and coverage is sufficient to support the
   proposed confidence. Stop rather than infer from stale or missing telemetry.
   Treat low coverage, unresolved ownership, or a recent activity signal as
   weaker evidence; do not compensate by raising confidence.

3. Evaluate only complete `groups` from the snapshot. Exclude every group with
   `safety.isHardBlocked`, `ownerResolved: false`, a safety flag, or changed /
   incomplete identities. Reason from group metrics and activity, not from a
   child process name or PID. Keep three distinct judgments:

   - **Confidence**: evidence that the group is currently unnecessary.
   - **Impact**: the reported group private-memory and sustained-CPU totals.
   - **Risk**: ProcLens' safety classification; it is not a confidence score.

4. Write a version-1 advisory using the exact root and complete member list
   from the chosen group. Set only `action: "investigate"`, use a unique safe
   `advisoryId`, cap `confidencePct` at 70, and add concise, privacy-safe
   evidence. Do not claim that a group is safe to close. Do not include paths,
   command lines, user names, secrets, environment values, or copied snapshot
   data in evidence.

5. Set `snapshotHash` and `snapshotGeneratedAtUtc` directly from the snapshot.
   Set `createdAtUtc` at analysis time and an expiry no later than one hour.
   Validate before importing:

   ```powershell
   ./scripts/Test-ProcLensAdvisory.ps1 -AdvisoryPath advisory.json -SnapshotPath snapshot.json
   & ProcLens.exe recommendations import --file advisory.json --minutes 30
   ```

   Treat a non-zero result as a refusal. Capture a new snapshot and reassess;
   never edit hashes, dates, identities, safety flags, or confidence to force
   acceptance. ProcLens recomputes all identity, safety, impact, and confidence
   checks during import and can still reject the document.

## Non-negotiable boundaries

- Do not use `taskkill`, `Stop-Process`, WMI/CIM control methods, shell close
  shortcuts, or any other process-control interface.
- Do not submit `closeGracefully`, `restart`, or `disableStartup`; agent import
  accepts only non-executing `investigate`.
- Do not bypass a hard block, the 70% agent ceiling, stale/undersampled evidence
  ceilings, or ProcLens' current confidence calculation.
- Do not collect or request executable paths, command lines, names of people or
  machines, environment variables, or user data. Use only the snapshot fields.
- Do not publish an advisory when it would misrepresent evidence. A useful
  result may be an explanation that no safe advisory can be submitted.

## Inspect existing recommendations

Use the queue only to explain current, persisted recommendations:

```powershell
& ProcLens.exe recommendations list > recommendations.json
```

Read `references/contracts.md` for document fields, safety blocks, freshness
rules, evidence limits, and the exact CLI contract. Read it before constructing
or validating an advisory.
