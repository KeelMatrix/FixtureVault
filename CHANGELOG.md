# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

### Fixed

- Rejects directories replaced by links before enumeration, including links introduced in any current ancestor of a queued directory, and reports repository-wide traversal failure as `FV-E015` without disclosing outside filenames. It uses platform-correct Linux filesystem ABI checks including ARM64, and treats fixture growth during bounded reads as an incomplete scan while accounting every byte read toward the aggregate limit.
- Hardened fixture, policy, and manifest reads at the open boundary, including bounded growth/shrinkage detection and rejection of special files.
- Added an aggregate 4,096-record / 1 MiB JSON-field budget for findings and skipped diagnostics, with explicit `FV-E017` incomplete-scan results instead of unbounded report construction or silent truncation.
- Detects non-empty connection-string `Password`/`Pwd` credentials case-insensitively after decoding supported JSON escapes, including `\u0022` and `\"` quoted delimiters and escaped whitespace. JSON-wrapped doubled-quote runs and serialized string boundaries remain supported, while empty, whitespace-only, and already-redacted semantic values are ignored without disclosing secrets.
- Guides non-Verify `FV006` remediation to valid UTF-8 without NUL characters so the documented repair clears the UTF-8 policy finding as well as any uninspectable-content error.
- Escaped control characters in human-readable paths without changing JSON path values.

## [0.1.0] - 2026-09-15

### Added

- Provides the `fixturevault` .NET tool for read-only auditing of snapshot and golden files without changing fixture contents.
- Supports Verify, Snapshooter, generic golden-file, manifest-backed orphan, portability, size, binary, sensitive-data, and path-policy checks with stable JSON output and CI exit codes.
- Creates `.fixturevault.json` through `fixturevault init` only when the policy file is absent, and preserves existing policy and fixture files.
- Requires `ci.strict` to be an explicit Boolean: `true` blocks findings, `false` reports warnings, and missing or invalid values fail with exit code `2`.
- Classifies accepted Verify binary baselines and configured binary extensions before text decoding, while reporting received or unexpected binary assets according to the fixture policy.
- Uses fail-closed sensitive-data configuration and limits repository-root fallback scans to supported fixture-shaped files; ordinary repository binaries remain outside the governed fixture set.
