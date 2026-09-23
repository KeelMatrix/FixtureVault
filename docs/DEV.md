# FixtureVault Development

This guide covers repository-local validation and package-consumer checks for FixtureVault contributors and maintainers. Consumer installation and tool usage are documented in the [README](https://github.com/KeelMatrix/FixtureVault#readme). For vulnerability reporting, see [SECURITY.md](../SECURITY.md).

## Prerequisites

- The .NET SDK selected by [`global.json`](../global.json): currently `8.0.424`, with `rollForward` set to `latestPatch` and `allowPrerelease` set to `false`. Install `8.0.424` or a compatible patch accepted by that policy; an arbitrary later feature-band SDK or a .NET 9, .NET 10, or .NET 11 SDK does not satisfy this repository prerequisite.
- PowerShell 7 (`pwsh`) for repository-owned validation scripts

Run the commands below from the repository root. They write disposable build and package output under `artifacts/`, which is ignored by Git.

## Validate the Solution

```powershell
dotnet restore KeelMatrix.FixtureVault.sln
dotnet build KeelMatrix.FixtureVault.sln -c Release --no-restore
dotnet test KeelMatrix.FixtureVault.sln -c Release --no-build
dotnet format KeelMatrix.FixtureVault.sln --verify-no-changes
```

Use `KEELMATRIX_NO_TELEMETRY=1` during local validation when a command runs the tool and should not emit telemetry.

The safety regressions cover ancestor-link replacement during repository-wide path-policy discovery, both directions of
a read-boundary change, and the aggregate diagnostic bound. The
aggregate bound applies to ordinary findings and skipped diagnostics as well as collision findings; exhaustion is
`FV-E017`, exit code `2`, `Completed=false`, and no successful-scan telemetry.

The CI matrix also runs the packed-tool consumer smoke on `ubuntu-24.04-arm`; that leg exercises `init`, a clean
scan, positive and negative connection-string cases in both console and JSON formats, sensitive-data reporting
without value disclosure, symbolic-link rejection, and FIFO rejection.

## Run the Tool from Source

```powershell
dotnet run --project src/KeelMatrix.FixtureVault -- init
dotnet run --project src/KeelMatrix.FixtureVault -- scan --format json
```

## Audit Dependencies

The repository-owned audit checks direct and transitive dependencies and fails closed when applicable advisory data is unavailable or unrecognized:

```powershell
pwsh -NoProfile -File ./scripts/audit-vulnerabilities.ps1 -SolutionPath KeelMatrix.FixtureVault.sln
```

## Build and Inspect the Package

After a successful Release build, create the package and symbols, inspect the actual archives, and run the isolated consumer smoke:

```powershell
dotnet pack src/KeelMatrix.FixtureVault/KeelMatrix.FixtureVault.csproj -c Release --no-build --include-symbols --p:SymbolPackageFormat=snupkg --output ./artifacts/packages
$expectedCommit = (git rev-parse HEAD).Trim()
pwsh -NoProfile -File ./scripts/inspect-package.ps1 -PackagePath ./artifacts/packages/KeelMatrix.FixtureVault.0.1.0.nupkg -SymbolsPackagePath ./artifacts/packages/KeelMatrix.FixtureVault.0.1.0.snupkg -ExpectedVersion 0.1.0 -ExpectedCommit $expectedCommit
pwsh -NoProfile -File ./scripts/package-consumer-smoke.ps1 -PackagePath ./artifacts/packages/KeelMatrix.FixtureVault.0.1.0.nupkg -ExpectedVersion 0.1.0
```

The smoke script installs only the built `.nupkg` from an isolated local feed and verifies `--help`, `init`, a clean scan, a blocking scan, exit codes, JSON output, and representative semantic JSON-escape connection-string `Password`/`Pwd` cases (including `\u0022` delimiters and escaped whitespace), including non-disclosure. For contract changes, use the [Durable Contract Change Checklist](SCHEMA_CHANGE_CHECKLIST.md).
