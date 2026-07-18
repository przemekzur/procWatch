# Changelog

All notable changes to ProcLens will be documented here. The project follows
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Windowless Windows tray application and local dashboard.
- Process lifecycle, ownership, system pressure, and resource history.
- Privacy-safe SQLite storage with configurable retention.
- Authenticated loopback API with request hardening.
- External JSON classification rules and optional ViriCrew example pack.
- Self-contained Windows x64 and Arm64 build pipeline.
- Conservative optimization queue with separate confidence, impact, risk,
  provenance, and user-controlled recommendation outcomes.
- Versioned privacy-safe agent snapshot, recommendation list/import CLI
  contracts, and the bundled `proclens-process-advisor` skill.
- Agent-advisory validation and import safeguards: fresh complete identities,
  privacy-safe evidence, non-executing `investigate` action, and a 70% agent
  confidence ceiling.
- Explicit graceful-close safeguards with authentication, immediate identity
  revalidation, audit outcomes, and no automatic or force termination.
