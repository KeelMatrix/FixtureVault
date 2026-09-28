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

One scan accepts at most 64 policy roots or `--root` overrides. Exceeding that limit returns `FV-E018`, exit code `2`, and no successful-scan telemetry. Filesystem traversal has one scan-wide budget of 100,000 entries and 1,000,000 logical path-work units shared by all active-root walks and repository-wide path-policy discovery; every directory entry and every path component/ancestor validation uses the same charge on every supported operating system. Exhaustion is distinct from an outside-path result: it returns exit code `2` with `completed: false` (`FV-E003` for active-root work and `FV-E015` for repository-wide work), never `FV008` or malformed-manifest `FV-E011`, and never successful-scan telemetry.

Physical identity may deduplicate traversal roots, but classification is complete for every distinct eligible repository-relative path alias, including hard-linked files whose names imply different fixture rules. Manifest membership uses exact repository-relative spelling after separator canonicalization only: backslash becomes slash, while dot segments, duplicate or trailing separators, leading `./`, case, and Unicode normalization remain distinct. Portability normalization is reserved for `FV003` collision grouping.

JSON reports include `filesDiscovered`, `filesInspected`, and `completed`. The discovered count is the number of eligible files found after filtering; the inspected count includes only files whose bounded read and content-dependent checks completed. An incomplete exit-`2` report has `completed: false` and never presents unchecked files as inspected. A completed scan has `completed: true`, including a completed scan with exit `1` for blocking findings.

## Important limitations

- FixtureVault is strictly read-only during `scan`; it has no mutation or auto-fix command.
- It has no snapshot assertion API and does not generate snapshots or serializer output.
- `FV007` detects high-confidence structured credentials without disclosing matched values. Valid JSON is inspected through a maximum depth of 64 containers; deeper valid JSON fails closed with `FV-E014` and exit code `2`, while malformed JSON remains eligible for raw-text inspection. See the [canonical detection grammar](https://github.com/KeelMatrix/FixtureVault/blob/main/docs/DETECTION_GRAMMAR.md) for ownership, depth, and boundary details.
- Orphan detection is conservative: a file is reported as orphaned only when a supported convention or explicit manifest proves the relationship.
- `FV006` keeps the documented UTF-8 rule for non-Verify text, and its remediation directs users to valid UTF-8 without NUL characters even when a declared UTF-16/UTF-32 file can be decoded for inspection. Verify encoding and newline tolerance are not asserted because FixtureVault cannot prove the repository's canonical `VerifierSettings`. Known binary extensions are classified from their paths before text decoding, while a UTF-16/UTF-32 byte-order mark is decoded so `FV007` still runs when the decoded text is trustworthy. Content that cannot be trusted is never reported as checked: bytes that no supported encoding decodes, and decoded text that contains `U+0000` — including text whose encoding a byte-order mark declared — appear as a per-file `FV-SKIP-ENCODING` diagnostic that names the observed condition, and, when sensitive-data detection is enabled, fail the scan closed with `FV-E016` and exit code `2`.
- Unsupported or ambiguous fixture conventions are reported as skipped rather than guessed, and no skip diagnostic is silent: `FV-SKIP-*` entries carry the code, the affected repository-relative path when one exists, and a fixed reason in both console and JSON output.
- Files and queued directories are opened and enumerated through bounded filesystem checks that revalidate their authorized filesystem identity and complete ancestor chain. The scan captures one trusted repository-boundary identity and never rebinds it after a root or ancestor replacement. A scan-wide 100,000-entry and 1,000,000-logical-path-work budget bounds both retained entries and validation work. Replaced links, ordinary same-name replacements, POSIX directory-stream errors, special files, file growth/shrinkage, and resource-limit failures return exit code `2`; repository-wide path-policy traversal or manifest containment exhaustion reports `FV-E015`. Human-readable paths escape control characters while JSON preserves their actual path values. Findings and skipped diagnostics share a 4,096-record / 1 MiB JSON-field budget; exhaustion returns `FV-E017`, an incomplete scan, exit code `2`, and no successful-scan telemetry.
- Windows, Linux (including ARM64), and macOS use their platform filesystem boundary checks; Linux ARM64 is covered by the package-consumer CI matrix.
- The v1 rule scope is fixed to the documented `FV001`–`FV008` rule families and the `net8.0` .NET tool target.

See the [repository README](https://github.com/KeelMatrix/FixtureVault/blob/main/README.md) for the complete policy, rule, output, and exit-code contract. For deeper details, see the [schema and compatibility checklist](https://github.com/KeelMatrix/FixtureVault/blob/main/docs/SCHEMA_CHANGE_CHECKLIST.md), [privacy policy](https://github.com/KeelMatrix/FixtureVault/blob/main/PRIVACY.md), [security policy](https://github.com/KeelMatrix/FixtureVault/blob/main/SECURITY.md), and [developer validation guide](https://github.com/KeelMatrix/FixtureVault/blob/main/docs/DEV.md).
