# Security policy

Please report security issues privately to the repository maintainers rather
than opening a public issue. Include affected versions, reproduction steps, and
the expected impact. Supported releases will be listed on the repository's
Security page when the first public release is published.

ProcLens deliberately listens only on IPv4 loopback. Network exposure, remote
dashboard access, and running as an elevated account are unsupported.

## Recommendation and agent safeguards

The dashboard accepts state-changing requests only through authenticated local
POST routes with the per-install token, bounded JSON bodies, and exact local
Host/Origin checks. Recommendation decisions and graceful-close outcomes are
recorded locally for audit, including whether an action succeeded, failed, was
blocked, or found a changed identity.

Graceful close is the sole process-control path. It requires an explicit browser
confirmation and revalidates PID plus start time immediately before action, then
reapplies every safety policy. ProcLens never automatically terminates a process
and provides no force-termination path. CLI agents can only import a
non-executing `investigate` advisory; import rechecks freshness, complete group
membership, identity, safety, impact, and confidence rather than trusting agent
input.

Agent transport is local command-line/file transport only. The privacy-safe
snapshot and advisory contract prohibit command lines, paths, credentials,
environment variables, user names, and machine names. No cloud call or remote
agent endpoint is required.
