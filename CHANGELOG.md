# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

### Changed

- Documents that private vulnerability reports are accepted by email only while GitHub private vulnerability reporting is disabled.
- Bounds one scan to 64 configured or CLI-selected roots and one scan-wide filesystem traversal budget of 100,000 entries plus 1,000,000 path operations shared across active-root and repository-wide discovery.
- Makes filesystem identity, containment, and case-collision handling follow resolved filesystem objects rather than the operating-system name.
- Treats POSIX directory-stream errors as incomplete scans with exit code `2` instead of accepting an uncertain end-of-directory.

## [0.1.0] - Planned (not yet published)

### Added

- Provides the `fixturevault` .NET tool for read-only auditing of snapshot and golden files without changing fixture contents.
- Supports Verify, Snapshooter, generic golden-file, manifest-backed orphan, portability, size, binary, sensitive-data, and path-policy checks with stable JSON output and CI exit codes.
- Creates `.fixturevault.json` through `fixturevault init` only when the policy file is absent, and preserves existing policy and fixture files.
- Requires `ci.strict` to be an explicit Boolean: `true` blocks findings, `false` reports warnings, and missing or invalid values fail with exit code `2`.
- Classifies accepted Verify binary baselines and configured binary extensions before text decoding, while reporting received or unexpected binary assets according to the fixture policy.
- Uses fail-closed sensitive-data configuration and limits repository-root fallback scans to supported fixture-shaped files; ordinary repository binaries remain outside the governed fixture set.
- Reports eligible files discovered, files whose bounded content checks completed, and whether the scan completed, so an incomplete result cannot present unchecked files as inspected.
- Rejects ambiguous policy and manifest JSON, preserves the repository boundary established by the Git worktree, and avoids following links or reparse points during bounded filesystem inspection.
- Provides terminal-safe human diagnostics with injective path escaping, bounded report diagnostics, conservative convention handling, and no matched sensitive values or fixture contents in reports.
- Supports long Windows paths through bounded dynamic final-path resolution and provides best-effort telemetry only after completed scans, with an explicit local opt-out.
