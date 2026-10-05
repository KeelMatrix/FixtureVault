# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

### Changed

- Delegates telemetry opt-out, deduplication, cadence, state, queueing, delivery, and failure handling to `KeelMatrix.Telemetry`, while retaining the successful-scan eligibility gate.
- Documents that private vulnerability reports are accepted by email only while GitHub private vulnerability reporting is disabled.
- Bounds one scan to 64 configured or CLI-selected roots and one scan-wide filesystem traversal budget of 100,000 entries plus 1,000,000 logical path-work units shared across active-root and repository-wide discovery, with the same directory-entry and path-component charges on every supported operating system; exhaustion remains a contextual incomplete `FV-E003`/ `FV-E015` error rather than `FV008` or `FV-E011`.
- Keeps filesystem identity for traversal-boundary and root deduplication while classifying every distinct eligible repository-relative alias, including hard-linked files with different fixture-rule suffixes.
- Requires manifest membership to use exact repository-relative spelling after separator canonicalization only; dot segments, duplicate/trailing separators, case, and Unicode normalization remain distinct, while portability normalization remains limited to `FV003` collision grouping.
- Preserves the scan-owned trusted repository boundary and fails closed when an authorized root, ancestor, or queued directory changes identity instead of rebinding to the substituted path.
- Treats POSIX directory-stream errors as incomplete scans with exit code `2` instead of accepting an uncertain end-of-directory.
- Prunes exact and recursive ignored directories before descendant enumeration in both active-root and repository-wide discovery, so ignored descendants do not consume traversal budgets, produce `FV-SKIP-REPARSE` diagnostics, or affect classification. POSIX names are still decoded strictly for entries encountered during governed traversal; an unrepresentable encountered name fails closed with `FV-E002`, while descendants behind an established ignored directory are not decoded. Replacement-decoded names are never used for authorization or inspection, fixture contents are not disclosed, and successful-scan telemetry is not activated for incomplete results.
- Canonicalizes configured roots from actual repository directory entries before classification, so alternate-spelling overlapping roots cannot manufacture report paths, `FV003` collisions, or manifest mismatches while hard-link aliases remain independently classified.
- Makes the dependency vulnerability gate derive every target framework from the solution projects, require complete direct-and-transitive package arrays for every project/framework, separately handle the vulnerability-only command's path-only clean results, and reject missing, empty, duplicate, mismatched, unrelated, malformed, unavailable, and vulnerable results.

## [0.1.0] - 2026-10-05

### Added

- Adds `fixturevault`, a read-only .NET tool for auditing snapshot and golden files for stale received artifacts, provable orphans, unexpected binaries, portability issues, sensitive data, and CI policy violations.
- Supports Verify, Snapshooter, approval-test, and configured golden-file workflows with repository-local policy, stable JSON reports, and CI exit codes; `fixturevault init` creates `.fixturevault.json` only when absent.
