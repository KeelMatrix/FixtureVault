# FixtureVault development guide

## Navigation

- `src/KeelMatrix.FixtureVault` contains the executable tool, policy loader, safe filesystem traversal, scanner rules, report contract, and telemetry adapter.
- `tests/KeelMatrix.FixtureVault.Tests` contains unit, fixture-repository, security, non-mutation, report, and CLI contract tests.
- `README.md` is the user-facing contract for policy, rules, output, and exit codes.
- `artifacts/` is disposable local build and package output and is ignored by Git.

## Commands

Restore and build the solution:

```text
dotnet restore KeelMatrix.FixtureVault.sln
dotnet build KeelMatrix.FixtureVault.sln -c Release --no-restore
dotnet test KeelMatrix.FixtureVault.sln -c Release --no-build
dotnet format KeelMatrix.FixtureVault.sln --verify-no-changes
dotnet pack src/KeelMatrix.FixtureVault/KeelMatrix.FixtureVault.csproj -c Release --no-build
```

Run the tool from source during focused development:

```text
dotnet run --project src/KeelMatrix.FixtureVault -- init
dotnet run --project src/KeelMatrix.FixtureVault -- scan --format json
```

## Invariants

- The package is one `net8.0` .NET tool with command `fixturevault`; it has no supported library API.
- `.fixturevault.json` and report schema version `1` are compatibility contracts.
- Rule IDs `FV001` through `FV008` are stable and must remain documented with their behavior.
- `scan` is strictly read-only. Do not add code that writes, deletes, or rewrites fixture files.
- Every configured root stays within the repository boundary; reparse points and symbolic links are never followed.
- Findings use repository-relative paths and never include matched sensitive values or fixture contents.
- Keep convention detection conservative. Do not turn an ambiguous file relationship into an orphan claim.
- Telemetry runs only after a completed scan and is best-effort; `KEELMATRIX_NO_TELEMETRY=1` is used for local validation.

## Validation strategy

Start with the matching test class for a scanner change, then run the test project, Release build, format verification, package inspection, and isolated package-consumer smoke. Use synthetic fixture repositories for filesystem behavior and compare their file bytes before and after scans to preserve the non-mutation guarantee. Record platform or network checks that cannot run locally rather than inferring their results.
