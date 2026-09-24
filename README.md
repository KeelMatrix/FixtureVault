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

Candidates are files matching an allowed fixture extension or a supported fixture convention; unexpected known binary extensions are additionally inspected under a configured fixture root other than the repository-root fallback. A Verify `*.received.*` artifact is reported as `FV001`; known-binary received artifacts are not decoded as text, while text received artifacts are decoded and content-inspected, including for sensitive data. A Verify `*.verified.*` baseline is decoded and content-inspected only when its bytes establish a text encoding and the decoded text contains no `U+0000`; a baseline whose bytes establish no text encoding, or whose decoded text contains `U+0000`, is reported per file as `FV-SKIP-ENCODING` instead of being accepted as a fully checked baseline. When `init` uses `.` because no `tests` directory exists, ordinary repository assets such as documentation images, PDFs, and ZIP archives remain outside the governed fixture set; fixture-looking extensions and supported convention names are still audited.

The v1 `sensitiveDataRules` policy supports only `high-confidence` (case-insensitive), which is also the default. An unknown value is invalid configuration and returns exit code `2`; it never disables sensitive-data detection silently.

High-confidence `FV007` detection classifies structured credential values independently across connection-string `Password`/`Pwd`, `AccountKey`/`SharedAccessKey`/`SharedAccessSignature`, API-key headers and URL queries, Basic/Bearer authorization, Cookie/Set-Cookie, and generic assignments. Generic assignment keys are case-insensitive: `ApiKey`/`api_key`/`api-key`, `ClientSecret`/`client_secret`/`client-secret`, `Password`, `Pwd`, `Secret`, and `Token`. Raw assignment keys may use matching single or double quotes, and both `=` and `:` are supported; decoded JSON property names use the same aliases. Raw fixture text preserves backslashes literally. Structurally valid JSON string values decode exactly once and query values URL-decode exactly once before their field grammar is parsed; quoted delimiters, doubled quotes, embedded separators, repeated keys, and serialized boundaries remain scoped to the parser that consumed them. Each parsed generic field owns only its exact key/operator/value span, so prefix text, unknown-key syntax, and text beyond comma or whitespace boundaries remain independently inspected. A clean field applies only to that field: later fields, lines, array items, nested objects, and sibling JSON values remain independently inspected, and any sensitive value wins. Azure-style assignments separate sibling values at semicolons, commas, or whitespace. Empty, whitespace-only, and finite accepted redaction markers are not findings. Matched values are never printed in console or JSON output.

`ci.strict` is required and must be a JSON boolean. It has no implicit default: omitting it, misspelling it, or changing its casing is a configuration error (`FV-E005`) and exits `2`. When `ci.strict` is `true`, findings block the scan with exit code `1`. When it is explicitly `false`, findings are reported as warnings and the scan exits `0`; configuration and execution errors always exit `2`. Findings reported next to an error, such as `FV-E016`, follow the same policy: they are `warning`/`warn` when `ci.strict` is `false` and `error`/`block` otherwise. `--strict` is a convenience override that turns strict behavior on for the current scan.

## Supported Conventions

The built-in hints are:

- `verify`: detects common `*.received.*` artifacts and split-mode `*.received/<file>` artifacts, and audits `*.verified.*` and split-mode `*.verified/<file>` baselines. A `*.verified.*` or split-mode baseline with a known binary extension is accepted and remains subject to `maxFileBytes`; binary `*.received.*` artifacts produce `FV001` without an additional `FV005`. For Verify text fixtures, FixtureVault does not assert encoding or newline style because the repository's canonical `VerifierSettings` are not available to this scanner; supported custom encodings, carriage returns, and trailing newlines are not blocking `FV006` findings. A fixture that declares UTF-16 or UTF-32 with a byte-order mark is decoded with that encoding so `FV007` still runs when the decoded text is trustworthy. A Verify baseline is never treated as an accepted, fully checked baseline unless its decoded text is free of `U+0000`: bytes that no supported encoding decodes, bytes that contain NUL characters without a byte-order mark, and declared UTF-8, UTF-16, or UTF-32 text whose decoded content contains `U+0000` are all reported as `FV-SKIP-ENCODING`.
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
| `FV003` | Two fixture paths differ only by case after Unicode normalization. Collision messages identify the group size, not every path in the group. | Rename one path so it is unique on all supported filesystems. |
| `FV004` | A fixture exceeds `maxFileBytes`. | Reduce the fixture or deliberately raise the policy limit. |
| `FV005` | An unexpected binary asset is present under a fixture root: a known binary extension that policy does not accept, or bytes that no supported encoding decodes and that contain NUL characters outside an accepted Verify baseline. Bytes that establish a text encoding are never classified as binary; text whose decoded content contains `U+0000` is uninspectable content instead. | Remove the binary asset, configure its extension deliberately, or keep it as a supported Verify baseline. |
| `FV006` | A non-Verify text fixture is not valid UTF-8, uses a non-UTF-8 encoding, cannot be decoded as text at all, or its decoded content contains `U+0000`. UTF-8 BOMs are accepted, and a UTF-16/UTF-32 BOM is decoded so content inspection can run when the decoded text is free of `U+0000`. Verify encoding and newline tolerance are not asserted because canonical Verify settings cannot be proven. | Save non-Verify text as valid UTF-8 without NUL characters. A declared UTF-16/UTF-32 encoding may be decoded for inspection, but it still violates the non-Verify UTF-8 policy. Verify text is left to the repository's configured Verify settings. Whenever the content cannot be trusted, the file is also reported as `FV-SKIP-ENCODING` and, with sensitive-data detection enabled, the scan fails closed with `FV-E016`. |
| `FV007` | A high-confidence sensitive-data pattern was detected in decoded text. Detection runs only on decoded text that contains no `U+0000`; when the content cannot be trusted the file is reported as `FV-SKIP-ENCODING` instead, and an enabled sensitive-data policy fails closed with `FV-E016`. | Remove the sensitive value from the fixture. The value is never printed. |
| `FV008` | A fixture-looking file is outside the approved roots. | Move it below an approved root or update `roots`. |

### Content Inspection and Encoding

Content inspection covers `FV006` for non-Verify text and `FV007` for every text fixture. It runs only when a fixture's bytes establish a text encoding and the decoded text contains no `U+0000`. FixtureVault establishes the encoding from the bytes themselves and never from a guess, and one decision — declared encoding, decodability, and decoded NUL state — decides the outcome for every fixture:

| Declared encoding | Decoded content | Outcome |
| --- | --- | --- |
| none (no byte-order mark) | valid UTF-8 text without `U+0000` | content inspected; `FV007` when a sensitive pattern is present |
| none (no byte-order mark) | valid UTF-8 text containing `U+0000` | uninspectable content |
| UTF-8 byte-order mark | valid UTF-8 text without `U+0000` | content inspected; no `FV006` |
| UTF-8 byte-order mark | text containing `U+0000` | uninspectable content |
| UTF-16LE/UTF-16BE byte-order mark | valid text without `U+0000` | content inspected; `FV007` still detected |
| UTF-16LE/UTF-16BE byte-order mark | text containing `U+0000` | uninspectable content |
| UTF-32LE/UTF-32BE byte-order mark | valid text without `U+0000` | content inspected; `FV007` still detected |
| UTF-32LE/UTF-32BE byte-order mark | text containing `U+0000` | uninspectable content |
| any declared encoding | bytes no supported encoding decodes | uninspectable content |
| none (no byte-order mark) | undecodable bytes that contain NUL characters | uninspectable content on Verify paths; unexpected binary asset (`FV005`) on other paths |
| known binary extension | never decoded by design | binary asset; accepted when policy allows the extension or the path is an accepted Verify baseline |

NUL characters never prove that a fixture is binary, and a byte-order mark never proves that decoded NUL content is safe to inspect. `U+0000` is a legal Unicode code point and a normal byte pattern in UTF-16 and UTF-32 text, so the decoded text decides: a declared UTF-16 or UTF-32 fixture whose content contains `U+0000` is uninspectable content rather than a fully checked baseline. FixtureVault also does not infer undeclared encodings, so a `*.verified.*` or split-mode `*.verified/<file>` baseline whose bytes establish no text encoding is reported as uninspected content rather than accepted. Only a known binary file extension, or bytes that no supported encoding decodes and that contain NUL characters outside an accepted Verify baseline, are classified as binary assets.

When a counted fixture's decoded text cannot be trusted, FixtureVault never presents the file as fully checked:

- the report contains a per-file `FV-SKIP-ENCODING` entry with the repository-relative path, in both console and JSON output;
- the reported reason describes the condition that was observed — NUL characters in the decoded content, or no encoding that could be established — and names the declared encoding whenever a byte-order mark declared one, so a file that has a byte-order mark is never described as lacking one;
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

Diagnostics do not echo raw untrusted CLI arguments or policy values. Human-readable paths escape control characters as `\\n`, `\\r`, `\\t`, `\\x1B`, or `\\uNNNN`; JSON keeps the actual repository-relative path value. Collision messages use a bounded group size rather than repeating every path. Findings and skipped diagnostics share an aggregate budget of 4,096 retained records and a conservative 1 MiB JSON-field estimate; the first limit reached returns `FV-E017` before the next diagnostic is retained or serialized.

## Exit Codes

- `0`: the scan completed without policy-blocking findings;
- `1`: the scan completed and policy-blocking findings exist;
- `2`: a configuration, input, filesystem, or execution error prevented a trustworthy scan.

Malformed `.fixturevault.json`, a missing configured root, an unsafe root path, or a missing/malformed enabled manifest returns `2`, never a false clean result. A scan that cannot honor an enabled sensitive-data policy because a counted fixture's content could not be inspected also returns `2` (`FV-E016`) instead of reporting a clean scan. Filesystem read failures, file replacement or growth or shrinkage detected at the open/read boundary, total-byte exhaustion, and a diagnostic budget exhaustion likewise return `2` and do not activate successful-scan telemetry.

## Troubleshooting

- **Missing policy:** `FV-E001` means `.fixturevault.json` is absent. Run `fixturevault init`, or create the policy file manually using the documented schema.
- **Malformed policy:** `FV-E005` means the policy is invalid, too large, or uses an unsupported schema. Check `version`, required arrays, extensions, roots, `sensitiveDataRules` (`high-confidence` is the only v1 value), and the required `ci.strict` JSON boolean. Omitting, misspelling, or changing the casing of `ci.strict` is a configuration error, not a non-strict default.
- **Missing or unsafe root:** `FV-E008` means a configured root does not exist, is not a directory, is outside the repository, or is a link. Use repository-relative directories that exist and do not traverse outside the repository.
- **Required manifest:** with the `fixturevault-manifest` convention enabled, a missing manifest returns `FV-E012` and a malformed or unsafe manifest returns `FV-E011`; create `.fixturevault.manifest.json` with explicit `activeBaselines`, or remove that convention when no manifest is maintained.
- **Ignored-path matching:** `FV-E013` means the deterministic ignored-path matcher could not complete within the scan safety bound. The scan exits `2` and does not treat the path as unignored; reduce the number or complexity of ignored paths, or split the scan into smaller roots.
- **Sensitive-data detection:** `FV-E014` means an enabled sensitive-data detector could not complete. The scan exits `2` with a fixed message and does not activate telemetry.
- **Path-policy discovery:** `FV-E015` means the repository-wide fixture-looking-file walk could not complete. The scan exits `2` instead of silently skipping an inaccessible subtree.
- **Safe file inspection:** `FV-E009` means a counted file was missing, replaced, not a regular file, linked/reparse, or changed while it was being read. `FV-E010` means the total byte budget was exceeded. These errors fail closed before a clean result or successful-scan telemetry.
- **Diagnostic budget:** `FV-E017` means the aggregate diagnostic safety bound was reached: findings and skipped diagnostics together exceeded 4,096 retained records or the conservative 1 MiB JSON-field estimate, or a filesystem/collision bound was reached. No further diagnostic is retained, the report is explicitly incomplete, and the scan exits `2`; reduce the directory/collision group or split the scan.
- **Uninspectable content:** `FV-E016` means a counted fixture's content could not be trusted while `sensitiveDataRules` enables detection: no supported encoding (UTF-8, or UTF-16/UTF-32 declared by a byte-order mark) decodes its bytes, or its decoded text contains `U+0000`, including when a byte-order mark declared the encoding. The scan exits `2`, the matching `FV-SKIP-ENCODING` entry names the file and states what was observed, and no clean result is reported. Save the fixture as text without NUL characters, or remove it from the governed fixture set.
- **Unsupported or skipped checks:** unknown convention hints appear as `FV-SKIP-CONVENTION`. Orphan checks for Verify, Snapshooter, and generic files appear as `FV-SKIP-ORPHAN` because no relationship was proved. Reparse points appear as `FV-SKIP-REPARSE`; their targets are not read. Fixtures whose content could not be trusted — bytes that no supported encoding decodes, or decoded text containing `U+0000` — appear as `FV-SKIP-ENCODING` with their repository-relative path and the observed reason, because content-dependent checks, including sensitive-data detection, did not run.
- **Exit codes:** `0` means a completed scan has no blocking findings, `1` means a completed scan has blocking findings, and `2` means an error prevented a trustworthy scan. Use `--format json` to inspect structured `findings`, `skipped`, and `errors`.

## Security and Privacy

See the [Privacy](https://github.com/KeelMatrix/FixtureVault/blob/main/PRIVACY.md) and [Security Policy](https://github.com/KeelMatrix/FixtureVault/blob/main/SECURITY.md) documents for the canonical product-specific policies.

Configured roots are hard boundaries. Relative roots and `--root` overrides must remain inside the repository root; traversal outside that boundary is rejected. Every directory component from the repository root to a selected root is checked for links and reparse points before scanning, and every queued directory is revalidated through its complete current ancestor chain immediately before and during enumeration. A replacement link or parent-directory link therefore fails conservatively with `FV-E015` during repository-wide path-policy discovery rather than allowing outside filenames into `FV008` findings. Each file is revalidated and opened under that boundary before bytes are read. Unix special files, including FIFOs, are rejected before a potentially blocking read. Repository-relative paths are used in reports. Symbolic links and Windows reparse points are never followed, including links that point outside an approved root. Link entries are reported as skipped without reading their targets.

FixtureVault bounds policy size, filesystem entries, total bytes read, and retained report diagnostics. Directory entries are enumerated incrementally before they are retained. Fixture, policy, and manifest bytes are read through bounded streams that detect growth or shrinkage instead of accepting a stale-length prefix. Findings and skipped diagnostics share a 4,096-record and conservative 1 MiB JSON-field budget, enforced before each diagnostic is retained or serialized; exhaustion returns `FV-E017`, `Completed=false`, exit code `2`, and no successful-scan telemetry. Case-collision diagnostics retain at most a bounded group and never repeat the entire group in every finding. Known binary extensions are classified from their paths before any text decoding is attempted, so those assets are never decoded as text. A fixture that declares UTF-16 or UTF-32 with a byte-order mark is decoded so content inspection can run, and decoded text containing `U+0000` is never inspected, whatever byte-order mark the file carries. Content that cannot be trusted — bytes no supported encoding decodes, or decoded NUL content — is reported as `FV-SKIP-ENCODING` with the observed reason and, when sensitive-data detection is enabled, fails the scan closed with `FV-E016` and exit code `2` instead of a clean result. Invalid or unsupported encodings in non-Verify text produce a bounded diagnostic; Verify encoding and newline tolerance are not asserted without canonical Verify settings. Scanning is strictly non-mutating.

Sensitive-data detection is separate from redaction: FixtureVault does not rewrite a fixture to clear a finding. FV007 classifies each structured credential independently across connection-string `Password`/`Pwd`, `AccountKey`/`SharedAccessKey`/`SharedAccessSignature`, API-key headers and URL queries, Basic/Bearer authorization, Cookie/Set-Cookie, and generic assignments. Case-insensitive generic keys are `ApiKey`/`api_key`/`api-key`, `ClientSecret`/`client_secret`/`client-secret`, `Password`, `Pwd`, `Secret`, and `Token`; raw assignment keys may be quoted and may use `=` or `:`, while decoded JSON property names use the same aliases. Raw text preserves literal backslashes; valid JSON string values decode once and query values URL-decode once before their field grammar is parsed. Parsed generic fields own only their exact key/operator/value spans; prefix text, unknown-key syntax, and text beyond comma or whitespace boundaries remain independently inspected. Clean fields do not terminate sibling inspection, and Azure-style sibling assignments use semicolon, comma, and whitespace boundaries. Empty, whitespace-only, and the finite accepted redaction-marker set are clean, while quote and backslash characters remaining after parsing are data. The matched value is never retained in a report or diagnostic.

FixtureVault does not upload fixture contents. After a successfully completed scan, it requests the minimal activation and weekly heartbeat signals from `KeelMatrix.Telemetry` 0.1.1. Telemetry is best-effort and cannot affect scan results. Installation and `init` do not activate telemetry. Disable it for a process with:

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

The tool targets .NET 8 and uses platform-appropriate filesystem and encoding APIs. It is designed for Windows, Linux (including ARM64), and macOS; Unix boundary checks use the host filesystem ABI and Linux ARM64 is covered by the package-consumer CI leg. The public GitHub Actions CI matrix validates the tool on all three operating systems. Case-colliding paths are reported using a case-insensitive, Unicode-normalized comparison so repositories can catch cross-filesystem hazards. Unsupported conventions and inaccessible linked paths are skipped conservatively, and fixtures whose text encoding is not proven are skipped with a per-file diagnostic instead of being reported as checked.

FixtureVault is not a snapshot assertion framework, serializer, mutation/fix command, auto-approval system, cloud vault, hosted service, binary forensic scanner, or broad replacement for secret scanners. It does not inspect arbitrary repository files beyond the lightweight path-policy check for fixture-looking files outside approved roots.

## License

FixtureVault is released under the [MIT License](https://github.com/KeelMatrix/FixtureVault/blob/main/LICENSE). Package copyright metadata identifies KeelMatrix.
