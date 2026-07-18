# Security Audit Report

## Summary

- **Project:** ProcLens (`przemekzur/procWatch`), public repository
- **Scan date:** 2026-07-18
- **Overall Risk:** Low in the remediated working tree
- **Findings:** 0 remaining; 2 Medium findings resolved
- **Standards:** CWE, OWASP Top 10 (2025), CVSS 4.0
- **Scan mode:** Quick Scan at `a23b10d`, followed by a focused fix recheck
- **Selected categories:** 01, 02, 03, 04, 12, 17, 27, 31, 33, 51
- **Detected features:** .NET 10 Windows desktop app, embedded loopback HTTP dashboard, JavaScript frontend, SQLite, token authentication, NuGet, GitHub Actions
- **Recheck candidates:** None remaining after the focused working-tree verification
- **Comparison:** Previous 2 findings | Current 0 | Resolved 2 | New 0

The application has a deliberately narrow local attack surface and several strong controls: loopback-only binding, a random per-install token, constant-time token comparison, strict host and route allowlists, response security headers, parameterized database operations, and command-line capture disabled by default. The two supply-chain findings from the initial scan have been remediated in the working tree. They become part of the repository baseline when the modified and newly generated files are committed.

## Coverage

- **Mode:** Repository Quick Scan
- **Inventory strategy:** Enumerated tracked manifests, production entry points, HTTP routes, browser rendering surfaces, database statements, persistence flows, workflow steps, and direct dependencies; traced selected source-to-sink paths; checked current and exposed Git revisions for credential signatures; queried NuGet's live advisory source.
- **Completeness:** Partial
- **Surfaces:** scanned-pass **10** / scanned-finding **0** / deferred **1**
  - **Scanned-pass:** Category 01 SQL statements; Category 02 browser rendering; Category 03 tracked and exposed-revision secrets; Category 04 dashboard authentication; Category 12 persistence and response disclosure; Category 17 embedded database security; Category 27 dependency restore integrity; Category 31 CI action integrity; Category 33 direct dependency use; Category 51 HTTP route exposure.
  - **Scanned-finding:** None remaining after the focused recheck.
  - **Deferred:** Category 33 whole-program dead-file/dead-export reachability. The Quick Scan verified all five direct package declarations but did not build a complete symbol graph.
- **Exclusions:** `.git/**`, `bin/**`, `obj/**`, and `artifacts/**` were excluded as generated or repository-internal surfaces. Tests were excluded as finding sources but inspected as validation evidence. Generated `obj/project.assets.json` was read only to verify the resolved dependency graph.
- **No suppression files:** No `.snitch-ignore`, project Snitch configuration, or previous audit report was present when the scan began.

## Resolved Findings

### 1. NuGet restore integrity and advisory gating

- **Severity:** Medium | CVSS 4.0: ~5.3
- **CWE:** CWE-1395 (Dependency on Vulnerable Third-Party Component)
- **OWASP:** A03:2025 Software Supply Chain Failures
- **Finding ID:** `dependency.missing-lockfile` @ `ProcLens.csproj::PackageReference`
- **Status:** Resolved in the working tree
- **Files:** `Directory.Build.props:3-8`, `ProcLens.csproj:17-18`, `.github/workflows/ci.yml:21-32`, `packages.lock.json`, `tests/ProcLens.Tests/packages.lock.json`
- **Evidence:**

  ```xml
  <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  <NuGetAudit>true</NuGetAudit>
  <NuGetAuditMode>all</NuGetAuditMode>
  <NuGetAuditLevel>low</NuGetAuditLevel>
  <RestoreLockedMode Condition="'$(CI)' == 'true'">true</RestoreLockedMode>
  <WarningsAsErrors Condition="'$(CI)' == 'true'">$(WarningsAsErrors);NU1901;NU1902;NU1903;NU1904</WarningsAsErrors>
  ```

  ```yaml
  - name: Restore app dependencies in locked mode
    run: dotnet restore ProcLens.csproj --locked-mode
  - name: Build with warnings as errors
    run: dotnet build ProcLens.csproj -c Release -r ${{ matrix.runtime }} --no-restore -p:TreatWarningsAsErrors=true
  ```

  Lockfiles now freeze the production and test dependency closures. `ProcLens.csproj:17-18` declares both supported runtime graphs so the same application lockfile validates x64 and ARM64 jobs. In CI, all advisory severities are promoted to errors and every implicit or explicit restore uses locked mode.
- **Verification:** CI-mode locked restores left both lockfile hashes unchanged. Both runtime targets built and published with zero warnings, all 8 tests passed, and NuGet's live advisory source reported no known vulnerable direct or transitive packages in either project.
- **Residual action:** Commit both lockfiles with the other remediation files; they are new working-tree files until then.
- **Priority:** P3 (Plan)
- **Confidence:** High

### 2. GitHub Actions are pinned to immutable commits

- **Severity:** Medium | CVSS 4.0: ~5.3
- **CWE:** CWE-829 (Inclusion of Functionality from Untrusted Control Sphere)
- **OWASP:** A03:2025 Software Supply Chain Failures
- **Finding ID:** `cicd.unpinned-action` @ `.github/workflows/ci.yml::build-test-publish.steps.uses`
- **Status:** Resolved in the working tree
- **File:** `.github/workflows/ci.yml:17-18,49`
- **Evidence:**

  | Line | Workflow reference |
  |---:|---|
  | 17 | `actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0` (`v7.0.0`) |
  | 18 | `actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68` (`v6.0.0`) |
  | 49 | `actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a` (`v7.0.1`) |

  Each action now uses a full 40-character commit identifier, with its release retained as an adjacent comment for maintainability.
- **Verification:** Each configured commit was compared with the current upstream tag reference and matched. The repository-wide workflow search found no remaining mutable action references.
- **Residual action:** Commit the workflow change. Keep the GitHub Actions Dependabot updater at `.github/dependabot.yml:10-13` so future pin updates remain reviewable.
- **Priority:** P3 (Plan)
- **Confidence:** Medium

## Validation Signals

### VS-001 Automated security-regression coverage

- **Status:** warn
- **Category Links:** 02, 04, 12, 17, 51
- **Evidence:** `.github/workflows/ci.yml:28-30` runs the test project. `tests/ProcLens.Tests/PrivacyTests.cs:5-35` covers redaction, path normalization, and command hashing; `tests/ProcLens.Tests/HistoryStoreTests.cs:9-35` covers database writes, reads, and retention. No automated test currently exercises invalid dashboard tokens, host rejection, route closure, or browser string encoding.
- **Impact:** Important local boundary controls are verified by code review and runtime probes but could regress without CI detection.
- **Recommended Action:** Add integration tests for missing/invalid tokens, invalid host headers, unsupported methods, unknown routes, and attacker-shaped process labels rendered by the dashboard.
- **Confidence:** high

### VS-003 CI build and packaged smoke test

- **Status:** pass
- **Category Links:** 31
- **Evidence:** `.github/workflows/ci.yml:21-52` performs locked restore, builds with warnings as errors, tests, publishes both supported Windows runtimes without another restore, starts the packaged executable, polls its loopback health route with a bounded retry loop, stops it in cleanup, and uploads the artifacts.
- **Impact:** The release path has automated build, test, packaging, and startup checks.
- **Recommended Action:** Preserve these checks and review lockfile/action-pin updates through Dependabot pull requests.
- **Confidence:** high

### VS-005 Sensitive-flow traceability

- **Status:** pass
- **Category Links:** 03, 12
- **Evidence:** Process command data originates at `Program.cs:580`, is sanitized at `Program.cs:581,329-336`, is persisted only behind the explicit opt-in at `Program.cs:216`, and command capture defaults to off at `AppSettings.cs:11`. `PRIVACY.md:11-15` documents both the opt-in and residual redactor limitations.
- **Impact:** Sensitive collection is minimized by default and its persistence path is explicit and reviewable.
- **Recommended Action:** Keep capture disabled by default and extend sanitizer tests whenever supported secret-argument forms change.
- **Confidence:** high

### VS-006 Runtime abuse guardrails

- **Status:** pass
- **Category Links:** 04, 51
- **Evidence:** `DashboardServer.cs:15,40-46` limits concurrent connections to eight; lines 61-63 impose a five-second header timeout; lines 139-157 cap request headers at 16 KiB; lines 160-168 clamp the history range.
- **Impact:** The embedded loopback server has bounded work and input sizes despite being intentionally lightweight.
- **Recommended Action:** Retain these bounds and include them in future dashboard integration tests.
- **Confidence:** high

## Passed Checks

- [x] **Category 01 — SQL injection:** All production SQLite statements in `HistoryStore.cs:43-200` are literal-only or use bound parameters. Dashboard range input flows from `DashboardServer.cs:98,160-168` through a typed clamp to `$cutoff` and `$bucket` bindings at `HistoryStore.cs:125-126`.
- [x] **Category 02 — Browser injection:** Locally influenced labels flow from `Program.cs:578` and `ClassificationRules.cs:15-44` through history/API projection to the browser encoder at `wwwroot/app.js:11`; string substitutions at lines 83-105 use that encoder. Chart values are numeric and forced through arithmetic and fixed-decimal conversion at lines 18-38. A restrictive content policy is sent at `DashboardServer.cs:188`.
- [x] **Category 03 — Hardcoded secrets:** No credential signatures, private-key markers, credential-bearing database URLs, or secret-valued literal assignments were found in the 31 tracked files or exposed Git revisions. The dashboard credential is generated from 24 random bytes at `AppSettings.cs:66`.
- [x] **Category 04 — Authentication:** `DashboardServer.cs:24` binds only to loopback; lines 76-100 enforce the host allowlist and check the token before telemetry construction; lines 130-136 use a length check and constant-time comparison. Runtime probes confirmed unauthenticated telemetry is rejected.
- [x] **Category 12 — Logging and data exposure:** Command-line persistence is disabled by default (`AppSettings.cs:11`), sanitized (`Program.cs:329-336`), and gated by explicit opt-in (`Program.cs:216`). HTTP errors are generic and responses carry no-store/no-referrer headers at `DashboardServer.cs:182-191`. No persistent logger sink was found.
- [x] **Category 17 — Database security:** Connections use `SqliteConnectionStringBuilder` at `HistoryStore.cs:22-28,78-84,100-106`; dashboard-controlled values are typed, bounded, and parameterized. No database credentials or remote database-error response surface exists.
- [x] **Category 27 — Dependency integrity:** `Directory.Build.props:3-8` enables lockfile generation, full transitive auditing, CI locked mode, and CI failure for all NuGet advisory levels. Both lockfiles remained unchanged during CI-mode verification, and NuGet's live source reported no known vulnerable direct or transitive packages.
- [x] **Category 31 — CI integrity and least privilege:** `.github/workflows/ci.yml:7-18,49` uses read-only repository permission, GitHub-hosted runners, ordinary push/pull-request triggers, a literal runtime matrix, and immutable action commits. No secrets or event-body expressions flow into script steps.
- [x] **Category 33 — Direct dependency use:** All five direct packages across the production and test manifests are justified by imports, runtime provider behavior, test host discovery, or test-source usage. The broader dead-code graph remains deferred as stated in Coverage.
- [x] **Category 51 — Debug endpoints:** `DashboardServer.cs:84-124` implements a closed route allowlist. The public health route returns only a fixed status at lines 85-88; unknown routes return 404 and exceptions are not serialized to clients.

## Audit Metadata

- **Grader:** Auto-skipped by Quick Scan policy.
- **Redaction hard-fail:** false
- **Redaction rewrites:** 0
- **Finding identity:** Stable `ruleId@anchor` identifiers are used for re-scan comparison.
- **Scan mutations:** The initial scan phase was read-only. Both confirmed fixes were applied only after explicit user selection and confirmation, then rechecked.

*Scanned by Snitch -- 69 built-in categories. Get the latest version at https://snitchplugin.com.*
