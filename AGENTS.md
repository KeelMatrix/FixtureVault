# FixtureVault Development Guide

## Navigation

- `src/KeelMatrix.FixtureVault` contains the executable tool, policy loader, safe filesystem traversal, scanner rules, report contract, and telemetry adapter.
- `tests/KeelMatrix.FixtureVault.Tests` contains unit, fixture-repository, security, non-mutation, report, and CLI contract tests.
- `README.md` is the user-facing contract for policy, rules, output, and exit codes.
- `docs/DEV.md` contains contributor validation and package-consumer checks.
- `artifacts/` is disposable local build and package output and is ignored by Git.

## Commands

Restore and build the solution:

```powershell
dotnet restore KeelMatrix.FixtureVault.sln
dotnet build KeelMatrix.FixtureVault.sln -c Release --no-restore
dotnet test KeelMatrix.FixtureVault.sln -c Release --no-build
dotnet format KeelMatrix.FixtureVault.sln --verify-no-changes
dotnet pack src/KeelMatrix.FixtureVault/KeelMatrix.FixtureVault.csproj -c Release --no-build
```

Run the tool from source during focused development:

```powershell
dotnet run --project src/KeelMatrix.FixtureVault -- init
dotnet run --project src/KeelMatrix.FixtureVault -- scan --format json
```

## Release Preparation

After finalizing the intended entry in `CHANGELOG.md`, run the same fail-closed contract used by the tag-triggered release workflow before creating a tag. The check must use the exact commit that will be tagged:

```powershell
$commit = (git rev-parse HEAD).Trim()
pwsh -NoProfile -File ./scripts/test-changelog-contract.ps1 `
  -ExpectedVersion 0.1.0 `
  -ExpectedPackageVersion 0.1.0 `
  -ExpectedCommit $commit
```

Do not create or push the release tag until this command passes on the finalized changelog commit.

## Invariants

- The package is one `net8.0` .NET tool with command `fixturevault`; it has no supported library API.
- `.fixturevault.json` and report schema version `1` are compatibility contracts.
- Rule IDs `FV001` through `FV008` are stable and must remain documented with their behavior. `FV-SKIP-*` skip codes and `FV-E0xx` error codes are part of the same documented contract. The single decision that says whether a counted fixture was decoded and inspected lives in `src/KeelMatrix.FixtureVault/ContentClassification.cs`: decoded text is inspectable only when a supported encoding (UTF-8 with or without a byte-order mark, or UTF-16/UTF-32 declared by a byte-order mark) decodes the bytes and the decoded text contains no `U+0000`. Content-dependent checks must never be skipped without a per-file diagnostic: every counted fixture is either content-inspected as text, or reported with a per-file `FV-SKIP-*` entry whose reason states the observed condition and names the declared encoding whenever a byte-order mark declared one. A fixture is only treated as a binary asset that is not text-inspected when its file name has a known binary extension, or when a non-Verify path holds bytes that no supported encoding decodes and that contain NUL characters. Encodings are never inferred, so a baseline whose bytes no supported encoding decodes, and a baseline whose decoded text contains `U+0000` — with or without a byte-order mark — must be reported as `FV-SKIP-ENCODING` rather than accepted, and content that cannot be trusted must fail closed with `FV-E016` whenever sensitive-data detection is enabled.
- `scan` is strictly read-only. Do not add code that writes, deletes, or rewrites fixture files.
- Every configured root stays within the repository boundary; reparse points and symbolic links are never followed.
- Findings use repository-relative paths and never include matched sensitive values or fixture contents.
- Keep convention detection conservative. Do not turn an ambiguous file relationship into an orphan claim.
- History hygiene validates complete reachable history with a closed author allowlist (`KeelMatrix` or Dependabot) and committer allowlist (`KeelMatrix`, Dependabot, or GitHub web-flow); the same `.githooks/check-history` policy is invoked by ordinary CI, history hygiene, and release validation.
- Telemetry runs only after a completed scan and is best-effort; `KEELMATRIX_NO_TELEMETRY=1` is used for local validation.

## Validation Strategy

Start with the matching test class for a scanner change, then run the test project, Release build, format verification, package inspection, and isolated package-consumer smoke. Use synthetic fixture repositories for filesystem behavior and compare their file bytes before and after scans to preserve the non-mutation guarantee. Record platform or network checks that cannot run locally rather than inferring their results.
