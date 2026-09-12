# KeelMatrix.FixtureVault

[![CI](https://github.com/KeelMatrix/FixtureVault/actions/workflows/ci.yml/badge.svg)](https://github.com/KeelMatrix/FixtureVault/actions/workflows/ci.yml)

Keep your existing snapshot framework. FixtureVault audits the files around it for stale received artifacts, provable orphans, leaked sensitive data, cross-platform path problems, and CI policy violations.

FixtureVault is a .NET tool for repositories that use Verify, Snapshooter, approval-test outputs, or configured golden files. Its `scan` command is strictly read-only. FixtureVault complements existing snapshot frameworks; it does not replace them and does not provide snapshot assertions.

## Install

Install the global tool:

```bash
dotnet tool install --global KeelMatrix.FixtureVault --version 0.1.0
```

Update or uninstall it with:

```bash
dotnet tool update --global KeelMatrix.FixtureVault
dotnet tool uninstall --global KeelMatrix.FixtureVault
```

## Quick start

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

Roots are repository-relative directories. Allowed extensions are suffixes, so `.golden` matches nested names such as `Orders/Create.golden`. Adding a known binary extension such as `.png` makes matching files inside an active root governed, accepted fixtures; they do not produce `FV005`. Unlisted binary files remain unexpected. `maxFileBytes` is bounded to 64 MiB; the default is 1 MiB. Ignored paths use `*` for one path segment and `**` for any number of segments; matching is deterministic across operating systems and ignores path-separator and casing differences.

Candidates are files matching an allowed fixture extension or a supported fixture convention; unexpected known binary extensions are additionally inspected under a configured fixture root other than the repository-root fallback. A Verify `*.verified.*` baseline is an accepted binary fixture, while a Verify `*.received.*` artifact is reported only as `FV001`; neither is decoded as text. When `init` uses `.` because no `tests` directory exists, ordinary repository assets such as documentation images, PDFs, and ZIP archives remain outside the governed fixture set; fixture-looking extensions and supported convention names are still audited.

The v1 `sensitiveDataRules` policy supports only `high-confidence` (case-insensitive), which is also the default. An unknown value is invalid configuration and returns exit code `2`; it never disables sensitive-data detection silently.

When `ci.strict` is `true`, findings block the scan with exit code `1`. When it is `false`, findings are reported as warnings and the scan exits `0`; configuration and execution errors always exit `2`. `--strict` is a convenience override that turns strict behavior on for the current scan.

## Supported conventions

The built-in hints are:

- `verify`: detects common `*.received.*` artifacts and split-mode `*.received/<file>` artifacts, and audits `*.verified.*` and split-mode `*.verified/<file>` baselines. Binary `*.verified.*` and split-mode baselines are accepted and remain subject to `maxFileBytes`; binary `*.received.*` artifacts produce `FV001` without an additional `FV005`. For Verify text fixtures, FixtureVault accepts UTF-8 with or without a BOM and requires LF-only bytes with no trailing newline.
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
| `FV005` | An unexpected binary asset is present under a fixture root; accepted Verify baselines and files covered by an explicitly allowed known-binary extension are excluded. | Remove the binary asset, configure its extension deliberately, or keep it as a supported Verify baseline. |
| `FV006` | A fixture is not valid UTF-8, uses a non-UTF-8 encoding, or violates a proven newline convention. UTF-8 BOMs are accepted. Verify text fixtures must use LF-only bytes and no trailing newline. Other supported conventions have no asserted newline style. | Save non-Verify text as valid UTF-8. Regenerate or save Verify text as UTF-8 with optional BOM, LF-only newlines, and no trailing newline. |
| `FV007` | A high-confidence sensitive-data pattern was detected. | Remove the sensitive value from the fixture. The value is never printed. |
| `FV008` | A fixture-looking file is outside the approved roots. | Move it below an approved root or update `roots`. |

### Conservative orphan detection

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

## JSON output

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

## Exit codes

- `0`: the scan completed without policy-blocking findings;
- `1`: the scan completed and policy-blocking findings exist;
- `2`: a configuration, input, filesystem, or execution error prevented a trustworthy scan.

Malformed `.fixturevault.json`, a missing configured root, an unsafe root path, or a missing/malformed enabled manifest returns `2`, never a false clean result.

## Troubleshooting

- **Missing policy:** `FV-E001` means `.fixturevault.json` is absent. Run `fixturevault init`, or create the policy file manually using the documented schema.
- **Malformed policy:** `FV-E005` means the policy is invalid, too large, or uses an unsupported schema. Check `version`, required arrays, extensions, roots, `sensitiveDataRules` (`high-confidence` is the only v1 value), and `ci.strict`.
- **Missing or unsafe root:** `FV-E008` means a configured root does not exist, is not a directory, is outside the repository, or is a link. Use repository-relative directories that exist and do not traverse outside the repository.
- **Required manifest:** with the `fixturevault-manifest` convention enabled, a missing manifest returns `FV-E012` and a malformed or unsafe manifest returns `FV-E011`; create `.fixturevault.manifest.json` with explicit `activeBaselines`, or remove that convention when no manifest is maintained.
- **Unsupported or skipped checks:** unknown convention hints appear as `FV-SKIP-CONVENTION`. Orphan checks for Verify, Snapshooter, and generic files appear as `FV-SKIP-ORPHAN` because no relationship was proved. Reparse points appear as `FV-SKIP-REPARSE`; their targets are not read.
- **Exit codes:** `0` means a completed scan has no blocking findings, `1` means a completed scan has blocking findings, and `2` means an error prevented a trustworthy scan. Use `--format json` to inspect structured `findings`, `skipped`, and `errors`.

## Security and privacy

Configured roots are hard boundaries. Relative roots and `--root` overrides must remain inside the repository root; traversal outside that boundary is rejected. Every directory component from the repository root to a selected root is checked for links and reparse points before scanning, so a root beneath an intermediate link fails conservatively. Repository-relative paths are used in reports. Symbolic links and Windows reparse points are never followed, including links that point outside an approved root. Link entries are reported as skipped without reading their targets.

FixtureVault bounds policy size, filesystem entries, and total bytes read. It does not decode known binary assets as text. Invalid or unsupported encodings produce a bounded diagnostic. Scanning is strictly non-mutating.

Sensitive-data detection is separate from redaction: FixtureVault does not rewrite a fixture to clear a finding. Detection uses hardened primitives from `KeelMatrix.Redaction` 0.1.0, but the matched value is never retained in a report or diagnostic.

FixtureVault does not upload fixture contents. After a successfully completed scan, it requests the minimal activation and weekly heartbeat signals from `KeelMatrix.Telemetry` 0.1.0. Telemetry is best-effort and cannot affect scan results. Installation and `init` do not activate telemetry. Disable it for a process with:

```powershell
$env:KEELMATRIX_NO_TELEMETRY = "1"
```

The shared telemetry package also supports repository-local opt-out through `keelmatrix.telemetry.json`, `.env.local`, or `.env`.

## Dependency vulnerability gate

The repository-owned audit runs the .NET package vulnerability check with both direct and transitive dependencies:

```powershell
pwsh -NoProfile -File ./scripts/audit-vulnerabilities.ps1 -SolutionPath KeelMatrix.FixtureVault.sln
```

The gate fails when an applicable vulnerability is reported, when the audit command fails, or when advisory data is empty, unavailable, or unrecognized. Normal CI and tag validation run this same command before packaging.

## CI example

Run the built-in CLI directly; no Action is required:

```yaml
- name: Audit fixtures
  run: fixturevault scan --format json
```

For a global tool installation, install it in an earlier step with `dotnet tool install --global KeelMatrix.FixtureVault --version 0.1.0` and add the .NET tools directory to the runner `PATH` as required by that runner.

## Package verification

After building the package, inspect the actual archive and run the consumer smoke from a clean temporary package cache:

```powershell
dotnet pack src/KeelMatrix.FixtureVault/KeelMatrix.FixtureVault.csproj -c Release --no-build --include-symbols --p:SymbolPackageFormat=snupkg --output ./artifacts/packages
pwsh -NoProfile -File ./scripts/inspect-package.ps1 -PackagePath ./artifacts/packages/KeelMatrix.FixtureVault.0.1.0.nupkg -SymbolsPackagePath ./artifacts/packages/KeelMatrix.FixtureVault.0.1.0.snupkg -ExpectedVersion 0.1.0
pwsh -NoProfile -File ./scripts/package-consumer-smoke.ps1 -PackagePath ./artifacts/packages/KeelMatrix.FixtureVault.0.1.0.nupkg -ExpectedVersion 0.1.0
```

The smoke script stages only that `.nupkg` in a local feed, uses a controlled `NuGet.config` with cleared sources and explicit source mapping, sets fresh `NUGET_PACKAGES` and HTTP-cache directories, and then proves `--help`, `init` in a no-tests repository containing ordinary binary assets, clean scan exit `0`, and blocking JSON scan exit `1`.

## Platform behavior and limitations

The tool targets .NET 8 and uses platform-neutral .NET filesystem and encoding APIs. It is designed for Windows, Linux, and macOS. The public GitHub Actions CI matrix validates the tool on all three operating systems. Case-colliding paths are reported using a case-insensitive, Unicode-normalized comparison so repositories can catch cross-filesystem hazards. Unsupported conventions and inaccessible linked paths are skipped conservatively.

FixtureVault is not a snapshot assertion framework, serializer, mutation/fix command, auto-approval system, cloud vault, hosted service, binary forensic scanner, or broad replacement for secret scanners. It does not inspect arbitrary repository files beyond the lightweight path-policy check for fixture-looking files outside approved roots.

## License

FixtureVault is released under the [MIT License](LICENSE). Package copyright metadata identifies KeelMatrix.
