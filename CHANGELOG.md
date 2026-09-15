# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

## [0.1.0] - 2026-09-15

### Added

- Provides the `fixturevault` .NET tool for read-only auditing of snapshot and golden files without changing fixture contents.
- Supports Verify, Snapshooter, generic golden-file, manifest-backed orphan, portability, size, binary, sensitive-data, and path-policy checks with stable JSON output and CI exit codes.
- Creates `.fixturevault.json` through `fixturevault init` only when the policy file is absent, and preserves existing policy and fixture files.
- Enforces explicit `ci.strict` policy values and reports invalid configuration instead of selecting an implicit scan mode.
- Handles accepted Verify binary baselines and configured binary extensions without text decoding, while reporting received or unexpected binary assets according to the fixture policy.
- Validates sensitive-data policy values fail closed and keeps ordinary repository binaries outside fallback fixture scans while auditing supported fixture-shaped files.
