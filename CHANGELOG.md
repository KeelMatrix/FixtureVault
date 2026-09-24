# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

### Fixed

- Rejects directories replaced by links before enumeration, including links introduced in any current ancestor of a queued directory, and reports repository-wide traversal failure as `FV-E015` without disclosing outside filenames. It uses platform-correct Linux filesystem ABI checks including ARM64, and treats fixture growth during bounded reads as an incomplete scan while accounting every byte read toward the aggregate limit.
- Hardened fixture, policy, and manifest reads at the open boundary, including bounded growth/shrinkage detection and rejection of special files.
- Added an aggregate 4,096-record / 1 MiB JSON-field budget for findings and skipped diagnostics, with explicit `FV-E017` incomplete-scan results instead of unbounded report construction or silent truncation.
- Provides FV007 value classification across connection-string credentials, Azure-style key assignments, API-key headers and queries, Basic/Bearer authorization, Cookie/Set-Cookie, and generic assignments. Generic keys use one case-insensitive alias grammar for `ApiKey`/`api_key`/`api-key`, `ClientSecret`/`client_secret`/`client-secret`, `Password`, `Pwd`, `Secret`, and `Token`, with unquoted or matching-quoted raw keys and both `=` and `:` operators; leading-only, trailing-only, and mismatched key quotes are not assignment syntax. Raw text preserves literal backslashes; valid JSON values and URL query values decode exactly once. Generic parsing owns only exact key/operator/value spans, leaving prefix, unknown-key, comma-separated, and whitespace-separated syntax independently inspected without re-scanning classified marker values. Clean fields do not suppress sensitive siblings. Azure siblings use semicolon/comma/whitespace boundaries, and an empty Azure value yields to the shared sibling grammar's `=` and guarded `:` forms while preserving URI-like values; only Azure credential keys are classified. Empty, whitespace, and accepted-marker values do not create findings.
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
