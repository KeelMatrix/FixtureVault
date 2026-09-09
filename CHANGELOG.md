# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

### Changed

- Packaging, archive-contract, and consumer-smoke hardening for the planned first public release.

## [0.1.0] - Planned (not yet published)

### Added

- Planned first public release of `KeelMatrix.FixtureVault`, a `fixturevault` .NET tool for auditing snapshot and golden files without changing fixture contents.
- Verify, Snapshooter, generic golden-file, manifest-backed orphan, portability, size, binary, sensitive-data, and path-policy checks with stable JSON output and exit codes.
- `fixturevault init` creates `.fixturevault.json` only when it is absent and never overwrites it or edits fixtures.
