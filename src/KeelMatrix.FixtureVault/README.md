# KeelMatrix.FixtureVault

Keep your existing snapshot framework. FixtureVault is a .NET tool whose `scan` command is read-only while it audits the snapshot and golden files around Verify, Snapshooter, approval tests, or configured golden-file workflows. It complements those frameworks; it does not replace them or provide snapshot assertions.

## Install

```bash
dotnet tool install --global KeelMatrix.FixtureVault
```

Update or uninstall the tool with:

```bash
dotnet tool update --global KeelMatrix.FixtureVault
dotnet tool uninstall --global KeelMatrix.FixtureVault
```

## Quick start

From the repository you want to audit:

```bash
fixturevault init
fixturevault scan
```

For a focused JSON report from an explicit root:

```bash
fixturevault scan --root tests --format json
```

`init` creates `.fixturevault.json` only when it does not already exist. `scan` reads fixture files and returns CI-friendly exit codes: `0` for no blocking findings, `1` for blocking findings, and `2` when an error prevents a trustworthy scan.

## Important limitations

- FixtureVault is strictly read-only during `scan`; it has no mutation or auto-fix command.
- It has no snapshot assertion API and does not generate snapshots or serializer output.
- Orphan detection is conservative: a file is reported as orphaned only when a supported convention or explicit manifest proves the relationship.
- High-confidence sensitive-data detection recognizes non-empty connection-string `Password` and `Pwd` values case-insensitively, honors doubled quote escapes, respects serialized string boundaries, and ignores empty, whitespace-only, and already-redacted values without printing matched values.
- `FV006` keeps the documented UTF-8 rule for non-Verify text, and its remediation directs users to valid UTF-8 without NUL characters even when a declared UTF-16/UTF-32 file can be decoded for inspection. Verify encoding and newline tolerance are not asserted because FixtureVault cannot prove the repository's canonical `VerifierSettings`. Known binary extensions are classified from their paths before text decoding, while a UTF-16/UTF-32 byte-order mark is decoded so `FV007` still runs when the decoded text is trustworthy. Content that cannot be trusted is never reported as checked: bytes that no supported encoding decodes, and decoded text that contains `U+0000` — including text whose encoding a byte-order mark declared — appear as a per-file `FV-SKIP-ENCODING` diagnostic that names the observed condition, and, when sensitive-data detection is enabled, fail the scan closed with `FV-E016` and exit code `2`.
- Unsupported or ambiguous fixture conventions are reported as skipped rather than guessed, and no skip diagnostic is silent: `FV-SKIP-*` entries carry the code, the affected repository-relative path when one exists, and a fixed reason in both console and JSON output.
- Files and queued directories are opened and enumerated through bounded filesystem checks that revalidate the complete ancestor chain. Replaced links, special files, file growth/shrinkage, and resource-limit failures return exit code `2`; repository-wide path-policy traversal failures report `FV-E015`. Human-readable paths escape control characters while JSON preserves their actual path values. Findings and skipped diagnostics share a 4,096-record / 1 MiB JSON-field budget; exhaustion returns `FV-E017`, an incomplete scan, exit code `2`, and no successful-scan telemetry.
- Windows, Linux (including ARM64), and macOS use their platform filesystem boundary checks; Linux ARM64 is covered by the package-consumer CI matrix.
- The v1 rule scope is fixed to the documented `FV001`–`FV008` rule families and the `net8.0` .NET tool target.

See the [repository README](https://github.com/KeelMatrix/FixtureVault/blob/main/README.md) for the complete policy, rule, output, and exit-code contract. For deeper details, see the [schema and compatibility checklist](https://github.com/KeelMatrix/FixtureVault/blob/main/docs/SCHEMA_CHANGE_CHECKLIST.md), [privacy policy](https://github.com/KeelMatrix/FixtureVault/blob/main/PRIVACY.md), [security policy](https://github.com/KeelMatrix/FixtureVault/blob/main/SECURITY.md), and [developer validation guide](https://github.com/KeelMatrix/FixtureVault/blob/main/docs/DEV.md).
