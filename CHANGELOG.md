# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

## [0.1.0] - Planned (not yet published)

### Added

- Planned first public release of `KeelMatrix.FixtureVault`, a `fixturevault` .NET tool for auditing snapshot and golden files without changing fixture contents.
- Verify, Snapshooter, generic golden-file, manifest-backed orphan, portability, size, binary, sensitive-data, and path-policy checks with stable JSON output and exit codes.
- `fixturevault init` creates `.fixturevault.json` only when it is absent and never overwrites it or edits fixtures.

### Changed

- Packaging, archive-contract, and consumer-smoke hardening for the planned first public release.
- Sensitive-data policy values now fail closed, and repository-root fallback scans no longer classify ordinary binary assets as fixtures.
- Verify binary `*.verified.*` baselines and explicitly allowed known-binary extensions are accepted without text decoding; binary `*.received.*` artifacts report `FV001` without a duplicate `FV005`, while unexpected binaries remain blocked.
- Release validation now accepts only the fixed first-release tag `v0.1.0` and inspects the exact tag-built packages before publication.
- Dependency vulnerability auditing now fails closed when applicable advisories are reported or advisory data is unavailable.
