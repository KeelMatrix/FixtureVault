# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

### Changed

- Documents that private vulnerability reports are accepted by email only while GitHub private vulnerability reporting is disabled.
- Bounds one scan to 64 configured or CLI-selected roots and one scan-wide filesystem traversal budget of 100,000 entries plus 1,000,000 logical path-work units shared across active-root and repository-wide discovery, with the same directory-entry and path-component charges on every supported operating system; exhaustion remains a contextual incomplete `FV-E003`/ `FV-E015` error rather than `FV008` or `FV-E011`.
- Keeps filesystem identity for traversal-boundary and root deduplication while classifying every distinct eligible repository-relative alias, including hard-linked files with different fixture-rule suffixes.
- Requires manifest membership to use exact repository-relative spelling after separator canonicalization only; dot segments, duplicate/trailing separators, case, and Unicode normalization remain distinct, while portability normalization remains limited to `FV003` collision grouping.
- Preserves the scan-owned trusted repository boundary and fails closed when an authorized root, ancestor, or queued directory changes identity instead of rebinding to the substituted path.
- Treats POSIX directory-stream errors as incomplete scans with exit code `2` instead of accepting an uncertain end-of-directory.
- Fails closed with `FV-E002` when any POSIX directory entry name within the scan boundary, including an exact or recursive ignored subtree, cannot be represented as strict UTF-8; replacement-decoded names are never used for authorization or inspection, fixture contents are not disclosed, and successful-scan telemetry is not activated.
- Canonicalizes configured roots from actual repository directory entries before classification, so alternate-spelling overlapping roots cannot manufacture report paths, `FV003` collisions, or manifest mismatches while hard-link aliases remain independently classified.
- Makes the dependency vulnerability gate derive every target framework from the solution projects, require complete direct-and-transitive package arrays for every project/framework, separately handle the vulnerability-only command's path-only clean results, and reject missing, empty, duplicate, mismatched, unrelated, malformed, unavailable, and vulnerable results.

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
