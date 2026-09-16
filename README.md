# KeelMatrix.FixtureVault

[![CI](https://github.com/KeelMatrix/FixtureVault/actions/workflows/ci.yml/badge.svg)](https://github.com/KeelMatrix/FixtureVault/actions/workflows/ci.yml)

Keep your existing snapshot framework. FixtureVault audits the files around it for stale received artifacts, provable orphans, leaked sensitive data, cross-platform path problems, and CI policy violations.

FixtureVault is a .NET tool for repositories that use Verify, Snapshooter, approval-test outputs, or configured golden files. Its `scan` command is strictly read-only. FixtureVault complements existing snapshot frameworks; it does not replace them and does not provide snapshot assertions.

## Install

Install the global tool from NuGet.org:

```bash
dotnet tool install --global KeelMatrix.FixtureVault
```

Update or uninstall it with:

```bash
dotnet tool update --global KeelMatrix.FixtureVault
dotnet tool uninstall --global KeelMatrix.FixtureVault
```

## Quick Start

From a repository root, create the policy file and scan the configured roots:

```bash
fixturevault init
fixturevault scan
```

`init` creates `.fixturevault.json` only when it does not already exist. It never overwrites that file and never changes fixture contents. If the file already exists, `init` reports that no files were changed.

When the repository has a `tests` directory, the default root is `tests`. Otherwise, `init` uses the repository root (`.`), which gives a new repository a usable clean-scan starting point without assuming a directory that does not exist.

Use an explicit root for a one-off scan. Repeating `--root` scans multiple roots and overrides the roots in the policy file:

```bash
fixturevault scan --root tests/Orders --root tests/Payments
fixturevault scan --root tests --format json
```

`scan` never creates, modifies, or deletes fixture files.

## Documentation

- [Policy and report contract](https://github.com/KeelMatrix/FixtureVault/blob/main/docs/SCHEMA_CHANGE_CHECKLIST.md)
- [Privacy](https://github.com/KeelMatrix/FixtureVault/blob/main/PRIVACY.md)
- [Security Policy](https://github.com/KeelMatrix/FixtureVault/blob/main/SECURITY.md)
- [Developer Guide](https://github.com/KeelMatrix/FixtureVault/blob/main/docs/DEV.md)

## Policy

The supported policy file name is `.fixturevault.json`. Its schema version is `1` and its complete v1 shape is:

```json
{
  "version": 1,
  "roots": ["tests"],
  "allowedExtensions": [".verified.json", ".verified.txt", ".snap", ".golden"],
  "maxFileBytes": 1048576,
  "conventions": ["verify", "snapshooter", "generic"],
  "sensitiveDataRules": ["high-confidence"],
  "ignoredPaths": ["**/.git/**", "**/bin/**", "**/obj/**"],
  "ci": {
    "strict": true
  }
}
```

Roots are repository-relative directories. Allowed extensions are suffixes, so `.golden` matches nested names such as `Orders/Create.golden`. Adding a known binary extension such as `.png` makes matching files inside an active root governed, accepted fixtures; they do not produce `FV005`. Unlisted binary files remain unexpected. `maxFileBytes` is bounded to 64 MiB; the default is 1 MiB. Ignored paths use `*` for zero or more non-separator characters in one path segment, `?` for exactly one non-separator character, and `**` for any number of segments; matching is deterministic across operating systems and ignores path-separator and casing differences. Ignored patterns must be repository-relative: leading `/` or `\`, UNC-shaped paths, drive-qualified forms such as `C:/...` or `C:\...`, and any `..` path segment are rejected with `FV-E007` and exit code `2`. Each pattern/path comparison has a bound based on the compiled pattern and normalized path lengths, and one scan permits at most 50,000,000 matcher state steps across all entries and ignore patterns. If that safety budget is exhausted, the scan fails with exit code `2` instead of inspecting an uncertain path.

Candidates are files matching an allowed fixture extension or a supported fixture convention; unexpected known binary extensions are additionally inspected under a configured fixture root other than the repository-root fallback. A Verify `*.received.*` artifact is reported as `FV001` and is never decoded as text. A Verify `*.verified.*` baseline is decoded and content-inspected when its bytes establish a text encoding; a baseline whose bytes establish no text encoding is reported per file as `FV-SKIP-ENCODING` instead of being accepted as a fully checked baseline. When `init` uses `.` because no `tests` directory exists, ordinary repository assets such as documentation images, PDFs, and ZIP archives remain outside the governed fixture set; fixture-looking extensions and supported convention names are still audited.

The v1 `sensitiveDataRules` policy supports only `high-confidence` (case-insensitive), which is also the default. An unknown value is invalid configuration and returns exit code `2`; it never disables sensitive-data detection silently.

`ci.strict` is required and must be a JSON boolean. It has no implicit default: omitting it, misspelling it, or changing its casing is a configuration error (`FV-E005`) and exits `2`. When `ci.strict` is `true`, findings block the scan with exit code `1`. When it is explicitly `false`, findings are reported as warnings and the scan exits `0`; configuration and execution errors always exit `2`. Findings reported next to an error, such as `FV-E016`, follow the same policy: they are `warning`/`warn` when `ci.strict` is `false` and `error`/`block` otherwise. `--strict` is a convenience override that turns strict behavior on for the current scan.

## Supported Conventions

The built-in hints are:

- `verify`: detects common `*.received.*` artifacts and split-mode `*.received/<file>` artifacts, and audits `*.verified.*` and split-mode `*.verified/<file>` baselines. A `*.verified.*` or split-mode baseline with a known binary extension is accepted and remains subject to `maxFileBytes`; binary `*.received.*` artifacts produce `FV001` without an additional `FV005`. For Verify text fixtures, FixtureVault does not assert encoding or newline style because the repository's canonical `VerifierSettings` are not available to this scanner; supported custom encodings, carriage returns, and trailing newlines are not blocking `FV006` findings. A fixture that declares UTF-16 or UTF-32 with a byte-order mark is decoded with that encoding so `FV007` still runs. A Verify baseline that proves no text encoding — bytes that no supported encoding decodes, or bytes that contain NUL characters without a byte-order mark — is reported as `FV-SKIP-ENCODING` instead of being treated as an accepted, fully checked baseline.
- `snapshooter`: audits ordinary `*.snap` files and treats `.snap` files below the documented `__snapshots__/__mismatch__/` directory as received/unapproved artifacts. Other `.snap` paths are audited as ordinary baselines; FixtureVault does not infer a mismatch convention from an ambiguous directory name.
- `generic`: audits files matching `allowedExtensions`, including `.golden` files.
- `fixturevault-manifest`: enables the explicit orphan proof described below. When enabled, the manifest is required; a missing manifest is a configuration error (exit code `2`).

Unknown hints are reported as skipped. FixtureVault does not infer a convention from arbitrary source code or test names.

To configure generic golden files, add their directory to `roots`, add `.golden` to `allowedExtensions`, and include `generic` in `conventions`:

```json
{
  "version": 1,
  "roots": ["tests", "goldens"],
  "allowedExtensions": [".verified.json", ".snap", ".golden"],
  "maxFileBytes": 1048576,
  "conventions": ["verify", "snapshooter", "generic"],
  "sensitiveDataRules": ["high-confidence"],
  "ignoredPaths": ["**/.git/**", "**/bin/**", "**/obj/**"],
  "ci": { "strict": true }
}
```

## Rules

The rule IDs below are the frozen v1 report contract. Every finding has a rule ID, severity, disposition, repository-relative path, explanation, and deterministic remediation.

| ID | Finding | Remediation |
| --- | --- | --- |
| `FV001` | A Verify `*.received.*` or split-mode `*.received/<file>` artifact, or a Snapshooter mismatch `.snap` file, is present without approval. | Review it and either approve it through the existing framework or remove it. |
| `FV002` | A baseline is absent from an explicit FixtureVault manifest. | Add it to the manifest or remove the stale baseline. |
| `FV003` | Two fixture paths differ only by case after Unicode normalization. | Rename one path so it is unique on all supported filesystems. |
| `FV004` | A fixture exceeds `maxFileBytes`. | Reduce the fixture or deliberately raise the policy limit. |
| `FV005` | An unexpected binary asset is present under a fixture root: a known binary extension that policy does not accept, or bytes that no supported encoding decodes and that contain NUL characters outside an accepted Verify baseline. Bytes that establish a text encoding are never classified as binary. | Remove the binary asset, configure its extension deliberately, or keep it as a supported Verify baseline. |
| `FV006` | A non-Verify text fixture is not valid UTF-8, uses a non-UTF-8 encoding, contains NUL characters without a byte-order mark, or violates a proven newline convention. UTF-8 BOMs are accepted, and a UTF-16/UTF-32 BOM is decoded so content inspection can still run. Verify encoding and newline tolerance are not asserted because canonical Verify settings cannot be proven. | Save non-Verify text as valid UTF-8 without NUL characters, or declare UTF-16/UTF-32 with a byte-order mark. Verify text is left to the repository's configured Verify settings. When no text encoding is proven, the file is also reported as `FV-SKIP-ENCODING` and, with sensitive-data detection enabled, the scan fails closed with `FV-E016`. |
| `FV007` | A high-confidence sensitive-data pattern was detected. Detection requires a proven text encoding; when none is proven the file is reported as `FV-SKIP-ENCODING` instead, and an enabled sensitive-data policy fails closed with `FV-E016`. | Remove the sensitive value from the fixture. The value is never printed. |
| `FV008` | A fixture-looking file is outside the approved roots. | Move it below an approved root or update `roots`. |

### Content Inspection and Encoding

Content inspection covers `FV006` for non-Verify text and `FV007` for every text fixture, and it depends on a proven text encoding. FixtureVault establishes the encoding from the bytes themselves and never from a guess: UTF-8 with or without a UTF-8 byte-order mark, or UTF-16/UTF-32 declared by a byte-order mark, which is decoded with that declared encoding.

NUL characters do not prove a fixture is binary. NUL is a legal UTF-8 code point, so bytes that decode as UTF-8 can also be a BOM-less UTF-16 or UTF-32 stream; FixtureVault does not infer undeclared encodings. Consequently a `*.verified.*` or split-mode `*.verified/<file>` baseline is decoded and content-inspected when its bytes establish a text encoding, and a baseline whose bytes contain NUL characters without a byte-order mark is reported as uninspected content rather than accepted. Only a known binary file extension, or bytes that no supported encoding decodes and that contain NUL characters outside an accepted Verify baseline, are classified as binary assets.

When no text encoding is proven for a counted fixture — no byte-order mark and bytes that are not valid UTF-8 or that contain NUL characters, or a declared UTF-16/UTF-32 fixture whose content is malformed — FixtureVault never presents the file as fully checked:

- the report contains a per-file `FV-SKIP-ENCODING` entry with the repository-relative path, in both console and JSON output;
- non-Verify text still produces the blocking `FV006` finding;
- when `sensitiveDataRules` enables detection, the scan additionally emits the fixed `FV-E016` error and exits `2`, because the configured sensitive-data policy cannot be honored for that file;
- the skip entry alone never changes the exit code; without an enabled sensitive-data policy it records the gap and the exit code follows the remaining findings. The v1 policy loader always enables `high-confidence`, so a policy-driven scan fails closed instead.

### Conservative Orphan Detection

Ordinary Verify, Snapshooter, and generic file names do not prove that a baseline is orphaned. FixtureVault therefore reports orphan detection as skipped for those conventions and never makes a heuristic orphan claim.

Teams that maintain an explicit complete baseline inventory can opt in to the `fixturevault-manifest` convention. Create `.fixturevault.manifest.json` at the repository root:

```json
{
  "version": 1,
  "activeBaselines": [
    "tests/Orders/Create.verified.json",
    "tests/Orders/List.snap"
  ]
}
```

With that hint enabled, a supported baseline not listed in `activeBaselines` produces `FV002`. The manifest is an explicit user-maintained proof boundary; FixtureVault does not create or update it.

## JSON Output

Use `--format json` for CI and automation. JSON report schema version `1` is stable and findings are the same findings shown by console output:

```json
{
  "schemaVersion": 1,
  "toolVersion": "0.1.0",
  "filesInspected": 2,
  "findings": [
    {
      "ruleId": "FV001",
      "severity": "error",
      "disposition": "block",
      "path": "tests/Orders/OrderTests.received.json",
      "message": "A received/unapproved snapshot artifact is present in the configured fixture tree.",
      "remediation": "Review it and either approve it through the existing framework or remove it."
    }
  ],
  "skipped": [],
  "errors": []
}
```

Sensitive findings contain only the path and rule information. No matched value, fixture content, file hash, or secret category is included.

Diagnostics do not echo raw untrusted CLI arguments or policy values. They use an argument position, a fixed category, or another bounded contract value instead.

## Exit Codes

- `0`: the scan completed without policy-blocking findings;
- `1`: the scan completed and policy-blocking findings exist;
- `2`: a configuration, input, filesystem, or execution error prevented a trustworthy scan.

Malformed `.fixturevault.json`, a missing configured root, an unsafe root path, or a missing/malformed enabled manifest returns `2`, never a false clean result. A scan that cannot honor an enabled sensitive-data policy because a counted fixture's content could not be inspected also returns `2` (`FV-E016`) instead of reporting a clean scan.

## Troubleshooting

- **Missing policy:** `FV-E001` means `.fixturevault.json` is absent. Run `fixturevault init`, or create the policy file manually using the documented schema.
- **Malformed policy:** `FV-E005` means the policy is invalid, too large, or uses an unsupported schema. Check `version`, required arrays, extensions, roots, `sensitiveDataRules` (`high-confidence` is the only v1 value), and the required `ci.strict` JSON boolean. Omitting, misspelling, or changing the casing of `ci.strict` is a configuration error, not a non-strict default.
- **Missing or unsafe root:** `FV-E008` means a configured root does not exist, is not a directory, is outside the repository, or is a link. Use repository-relative directories that exist and do not traverse outside the repository.
- **Required manifest:** with the `fixturevault-manifest` convention enabled, a missing manifest returns `FV-E012` and a malformed or unsafe manifest returns `FV-E011`; create `.fixturevault.manifest.json` with explicit `activeBaselines`, or remove that convention when no manifest is maintained.
- **Ignored-path matching:** `FV-E013` means the deterministic ignored-path matcher could not complete within the scan safety bound. The scan exits `2` and does not treat the path as unignored; reduce the number or complexity of ignored paths, or split the scan into smaller roots.
- **Sensitive-data detection:** `FV-E014` means an enabled sensitive-data detector could not complete. The scan exits `2` with a fixed message and does not activate telemetry.
- **Path-policy discovery:** `FV-E015` means the repository-wide fixture-looking-file walk could not complete. The scan exits `2` instead of silently skipping an inaccessible subtree.
- **Uninspectable content:** `FV-E016` means a counted fixture proved no text encoding while `sensitiveDataRules` enables detection: either no supported encoding (UTF-8, or UTF-16/UTF-32 declared by a byte-order mark) decodes its bytes, or its bytes contain NUL characters without a byte-order mark. The scan exits `2`, the matching `FV-SKIP-ENCODING` entry names the file, and no clean result is reported. Save the fixture as UTF-8 text without NUL characters, declare UTF-16/UTF-32 with a byte-order mark, or remove it from the governed fixture set.
- **Unsupported or skipped checks:** unknown convention hints appear as `FV-SKIP-CONVENTION`. Orphan checks for Verify, Snapshooter, and generic files appear as `FV-SKIP-ORPHAN` because no relationship was proved. Reparse points appear as `FV-SKIP-REPARSE`; their targets are not read. Fixtures whose text encoding could not be proven — including bytes that contain NUL characters without a byte-order mark — appear as `FV-SKIP-ENCODING` with their repository-relative path because content-dependent checks, including sensitive-data detection, did not run.
- **Exit codes:** `0` means a completed scan has no blocking findings, `1` means a completed scan has blocking findings, and `2` means an error prevented a trustworthy scan. Use `--format json` to inspect structured `findings`, `skipped`, and `errors`.

## Security and Privacy

See the [Privacy](https://github.com/KeelMatrix/FixtureVault/blob/main/PRIVACY.md) and [Security Policy](https://github.com/KeelMatrix/FixtureVault/blob/main/SECURITY.md) documents for the canonical product-specific policies.

Configured roots are hard boundaries. Relative roots and `--root` overrides must remain inside the repository root; traversal outside that boundary is rejected. Every directory component from the repository root to a selected root is checked for links and reparse points before scanning, so a root beneath an intermediate link fails conservatively. Repository-relative paths are used in reports. Symbolic links and Windows reparse points are never followed, including links that point outside an approved root. Link entries are reported as skipped without reading their targets.

FixtureVault bounds policy size, filesystem entries, and total bytes read. It does not decode known binary assets as text. A fixture that declares UTF-16 or UTF-32 with a byte-order mark is decoded so content inspection can run; content that proves no text encoding, including bytes that contain NUL characters without a byte-order mark, is reported as `FV-SKIP-ENCODING` and, when sensitive-data detection is enabled, fails the scan closed with `FV-E016` and exit code `2` instead of a clean result. Invalid or unsupported encodings in non-Verify text produce a bounded diagnostic; Verify encoding and newline tolerance are not asserted without canonical Verify settings. Scanning is strictly non-mutating.

Sensitive-data detection is separate from redaction: FixtureVault does not rewrite a fixture to clear a finding. Detection uses hardened primitives from `KeelMatrix.Redaction` 0.1.0, but the matched value is never retained in a report or diagnostic.

FixtureVault does not upload fixture contents. After a successfully completed scan, it requests the minimal activation and weekly heartbeat signals from `KeelMatrix.Telemetry` 0.1.0. Telemetry is best-effort and cannot affect scan results. Installation and `init` do not activate telemetry. Disable it for a process with:

```powershell
$env:KEELMATRIX_NO_TELEMETRY = "1"
```

The shared telemetry package also supports repository-local opt-out through `keelmatrix.telemetry.json`, `.env.local`, or `.env`.

## CI Example

Run the built-in CLI directly; no Action is required:

```yaml
- name: Audit fixtures
  run: fixturevault scan --format json
```

For a global tool installation, install it in an earlier step and add the .NET tools directory to the runner `PATH` as required by that runner:

```bash
dotnet tool install --global KeelMatrix.FixtureVault
```

## Platform Behavior and Limitations

The tool targets .NET 8 and uses platform-neutral .NET filesystem and encoding APIs. It is designed for Windows, Linux, and macOS. The public GitHub Actions CI matrix validates the tool on all three operating systems. Case-colliding paths are reported using a case-insensitive, Unicode-normalized comparison so repositories can catch cross-filesystem hazards. Unsupported conventions and inaccessible linked paths are skipped conservatively, and fixtures whose text encoding is not proven are skipped with a per-file diagnostic instead of being reported as checked.

FixtureVault is not a snapshot assertion framework, serializer, mutation/fix command, auto-approval system, cloud vault, hosted service, binary forensic scanner, or broad replacement for secret scanners. It does not inspect arbitrary repository files beyond the lightweight path-policy check for fixture-looking files outside approved roots.

## License

FixtureVault is released under the [MIT License](https://github.com/KeelMatrix/FixtureVault/blob/main/LICENSE). Package copyright metadata identifies KeelMatrix.
