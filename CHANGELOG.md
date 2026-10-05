# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

## [0.1.0] - 2026-10-05

### Added

- Provides `fixturevault`, a .NET 8 tool for auditing snapshot and golden-file repositories while keeping existing snapshot frameworks in place; `fixturevault init` creates `.fixturevault.json` only when absent, and `fixturevault scan` is read-only.
- Supports Verify, Snapshooter, approval-test outputs, configured golden files, and explicit manifest-backed orphan checks through repository-local policy with conservative convention handling.
- Reports stable `FV001`–`FV008` rule families for received artifacts, manifest-proven orphans, cross-filesystem path collisions, oversized fixtures, unexpected binaries, encoding and content problems, high-confidence sensitive data, and fixture-shaped files outside approved roots.
- Provides versioned JSON reports and human-readable console output with deterministic CI exit codes, explicit strict-mode behavior, and separate discovered, inspected, and completion state; supports Windows, Linux including ARM64, and macOS.
- Enforces hard repository and configured-root filesystem boundaries with no-follow link/reparse handling, filesystem-identity revalidation, strict POSIX filename decoding, ignored-subtree pruning, and bounded root, traversal, matcher, and diagnostic work that fails closed rather than returning an untrustworthy clean result.
- Handles text and binary fixtures conservatively and detects high-confidence structured credentials without printing matched values or fixture contents.
- Keeps fixture analysis local and uses `KeelMatrix.Telemetry` only after successfully completed scans for best-effort activation and weekly-heartbeat signals; telemetry is suppressible with `KEELMATRIX_NO_TELEMETRY=1` and does not include fixture names, paths, findings, counts, contents, hashes, matched values, or policy contents.
