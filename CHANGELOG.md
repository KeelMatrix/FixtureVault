# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

### Fixed

- FV007 now scopes connection-string masking to owned value spans, so unrelated context cannot suppress credentials while quoted non-secret values remain clean. The complete grammar is maintained in [DETECTION_GRAMMAR.md](docs/DETECTION_GRAMMAR.md).
- Hardened FV007 parser boundaries so quoted non-secret values cannot consume whitespace-separated credentials, valid JSON deeper than the supported 64-container depth fails closed with `FV-E014`, and unquoted whitespace lookahead advances linearly instead of rescanning the same run.

- History validation now accepts approved KeelMatrix, Dependabot, and GitHub web-flow identity combinations while remaining fail-closed for unauthorized attribution.

- Package inspection now requires complete primary and symbol archive contents, matching symbol provenance, and byte-identical primary and symbol PDB data before publication.

- Rejects directories replaced by links before enumeration, including links introduced in any current ancestor of a queued directory, and reports repository-wide traversal failure as `FV-E015` without disclosing outside filenames. It uses platform-correct Linux filesystem ABI checks including ARM64, and treats fixture growth during bounded reads as an incomplete scan while accounting every byte read toward the aggregate limit.
- Hardened fixture, policy, and manifest reads at the open boundary, including bounded growth/shrinkage detection and rejection of special files.
- Added an aggregate 4,096-record / 1 MiB JSON-field budget for findings and skipped diagnostics, with explicit `FV-E017` incomplete-scan results instead of unbounded report construction or silent truncation.
- FV007 classification and ownership details are maintained in the [canonical detection grammar](docs/DETECTION_GRAMMAR.md).
- Embedded Basic/Bearer headers now classify marker versus genuine values at supported log-prefix locations, Azure prefix ownership remains bounded to the recognized key, and sibling lookahead is local to the current cursor for linear repeated-field work.
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
