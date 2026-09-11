[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Contract {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-JobTimeout {
    param(
        [string]$JobName,
        [string]$JobText,
        [int]$ExpectedMinutes
    )

    $timeoutMatch = [Text.RegularExpressions.Regex]::Match(
        $JobText,
        '(?m)^    timeout-minutes:\s*(\d+)\s*$')
    Assert-Contract $timeoutMatch.Success "$JobName must declare a job-level timeout-minutes bound."
    Assert-Contract ([int]$timeoutMatch.Groups[1].Value -eq $ExpectedMinutes) "$JobName must use timeout-minutes: $ExpectedMinutes."
}

function Assert-AuditBeforePack {
    param(
        [string]$WorkflowName,
        [string]$WorkflowText
    )

    $auditIndex = $WorkflowText.IndexOf("audit-vulnerabilities.ps1", [StringComparison]::Ordinal)
    $packIndex = $WorkflowText.IndexOf("dotnet pack", [StringComparison]::Ordinal)
    Assert-Contract ($auditIndex -ge 0) "$WorkflowName must run the repository vulnerability audit."
    Assert-Contract ($packIndex -ge 0) "$WorkflowName must pack the tool."
    Assert-Contract ($auditIndex -lt $packIndex) "$WorkflowName must audit vulnerable dependencies before packing."
}

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$workflowPath = Join-Path $repositoryRoot ".github/workflows/release.yml"
$ciWorkflowPath = Join-Path $repositoryRoot ".github/workflows/ci.yml"
$tagScriptPath = Join-Path $repositoryRoot "scripts/validate-release-tag.ps1"
$changelogScriptPath = Join-Path $repositoryRoot "scripts/test-changelog-contract.ps1"
$workflow = [IO.File]::ReadAllText($workflowPath)
$ciWorkflow = [IO.File]::ReadAllText($ciWorkflowPath)

$validationMatch = [Text.RegularExpressions.Regex]::Match(
    $workflow,
    '(?ms)^  validate-release:.*?(?=^  publish:)')
$publicationMatch = [Text.RegularExpressions.Regex]::Match(
    $workflow,
    '(?ms)^  publish:.*$')
Assert-Contract $validationMatch.Success "The release workflow is missing the validate-release job."
Assert-Contract $publicationMatch.Success "The release workflow is missing the publish job."

$validation = $validationMatch.Value
$publication = $publicationMatch.Value
Assert-JobTimeout "CI validate" ([Text.RegularExpressions.Regex]::Match(
    $ciWorkflow,
    '(?ms)^  validate:.*?(?=^  package-consumer-smoke:)').Value) 30
Assert-JobTimeout "CI package-consumer-smoke" ([Text.RegularExpressions.Regex]::Match(
    $ciWorkflow,
    '(?ms)^  package-consumer-smoke:.*$').Value) 20
Assert-JobTimeout "Release validate-release" $validation 45
Assert-JobTimeout "Release publish" $publication 20
Assert-Contract (-not $validation.Contains("id-token: write", [StringComparison]::Ordinal)) "The release validation job must not request id-token: write."
Assert-Contract ($validation.Contains("dotnet restore", [StringComparison]::Ordinal)) "Release validation must restore the solution."
Assert-Contract ($validation.Contains("dotnet build", [StringComparison]::Ordinal)) "Release validation must build the solution."
Assert-Contract ($validation.Contains("dotnet test", [StringComparison]::Ordinal)) "Release validation must test the solution."
Assert-Contract ($validation.Contains("dotnet pack", [StringComparison]::Ordinal)) "Release validation must pack the tool."
Assert-Contract ($validation.Contains("test-changelog-contract.ps1", [StringComparison]::Ordinal)) "Release validation must run the changelog/version contract."
Assert-Contract ($validation.IndexOf("test-changelog-contract.ps1", [StringComparison]::Ordinal) -lt $validation.IndexOf("dotnet pack", [StringComparison]::Ordinal)) "The changelog/version contract must run before release packing."
Assert-Contract ($validation.Contains('EXPECTED_COMMIT: ${{ github.sha }}', [StringComparison]::Ordinal)) "Release validation must bind changelog checks to the checked-out commit."
Assert-Contract ($validation.Contains('-ExpectedPackageVersion', [StringComparison]::Ordinal)) "Release validation must pass the expected package version to the changelog contract."
Assert-Contract ($validation.Contains('-ExpectedCommit $env:EXPECTED_COMMIT', [StringComparison]::Ordinal)) "Release validation must pass the expected commit to the changelog contract."
Assert-Contract ($validation.Contains("inspect-package.ps1", [StringComparison]::Ordinal)) "Release validation must inspect the package archives."
Assert-Contract ($validation.Contains("package-consumer-smoke.ps1", [StringComparison]::Ordinal)) "Release validation must run the package consumer smoke."
Assert-Contract ($validation.Contains("audit-vulnerabilities.ps1", [StringComparison]::Ordinal)) "Release validation must run the repository vulnerability audit."
Assert-Contract ($validation.Contains('KeelMatrix.FixtureVault.${{ steps.release-version.outputs.version }}.nupkg', [StringComparison]::Ordinal)) "Release validation must upload the primary package by exact name."
Assert-Contract ($validation.Contains('KeelMatrix.FixtureVault.${{ steps.release-version.outputs.version }}.snupkg', [StringComparison]::Ordinal)) "Release validation must upload the symbols package by exact name."
Assert-Contract ($validation.Contains('RELEASE_TAG: ${{ github.ref_name }}', [StringComparison]::Ordinal)) "The release ref must be passed through RELEASE_TAG."
$refExpressions = [Text.RegularExpressions.Regex]::Matches($validation, '\$\{\{ github\.ref_name \}\}')
Assert-Contract ($refExpressions.Count -eq 1) "The release ref must not be interpolated into an inline shell program."

Assert-Contract ($publication.Contains("id-token: write", [StringComparison]::Ordinal)) "The publication job must request id-token: write."
Assert-Contract ($publication.Contains("NuGet/login@v1", [StringComparison]::Ordinal)) "The publication job must use NuGet/login@v1."
Assert-Contract ($publication.Contains("user: dmitriyzen", [StringComparison]::Ordinal)) "The publication job must authenticate as dmitriyzen."
Assert-Contract (-not $publication.Contains("actions/checkout", [StringComparison]::Ordinal)) "The publication job must not check out product source."
Assert-Contract (-not $publication.Contains("--skip-duplicate", [StringComparison]::Ordinal)) "Publication must fail when an immutable version already exists."
Assert-Contract (([Text.RegularExpressions.Regex]::Matches($publication, 'dotnet nuget push')).Count -eq 2) "Publication must push exactly two artifacts."
Assert-Contract ($publication.Contains("--no-symbols", [StringComparison]::Ordinal)) "The primary package push must not publish symbols implicitly."
Assert-Contract ($publication.Contains(".snupkg", [StringComparison]::Ordinal)) "Publication must push the symbols package explicitly."
Assert-Contract ($ciWorkflow.Contains("audit-vulnerabilities.ps1", [StringComparison]::Ordinal)) "Normal CI must run the repository vulnerability audit."
Assert-Contract (Test-Path -LiteralPath $changelogScriptPath -PathType Leaf) "The changelog/version contract script is missing."
Assert-AuditBeforePack "Normal CI" $ciWorkflow
Assert-AuditBeforePack "Release validation" $workflow

$previousTag = $env:RELEASE_TAG
$previousOutput = $env:GITHUB_OUTPUT
$script:RepositoryCommit = (& git -C $repositoryRoot rev-parse HEAD 2>&1 | Out-String).Trim()
$gitExitCode = $LASTEXITCODE
Assert-Contract ($gitExitCode -eq 0 -and $script:RepositoryCommit -match '^[0-9a-fA-F]{40,64}$') "Could not resolve the repository commit for changelog contract tests."
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("fixturevault-changelog-contract-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
$fixtureChangelogPath = Join-Path $fixtureRoot "CHANGELOG.md"
$today = [DateTime]::UtcNow.ToString("yyyy-MM-dd")

function Invoke-ChangelogContract {
    param(
        [string]$Content,
        [string]$Version = "0.1.0",
        [string]$PackageVersion = "0.1.0",
        [string]$Commit = $script:RepositoryCommit
    )

    [IO.File]::WriteAllText($fixtureChangelogPath, $Content, [Text.UTF8Encoding]::new($false))
    $output = & pwsh -NoProfile -File $changelogScriptPath `
        -ExpectedVersion $Version `
        -ChangelogPath $fixtureChangelogPath `
        -ExpectedPackageVersion $PackageVersion `
        -ExpectedCommit $Commit `
        -RepositoryRoot $repositoryRoot 2>&1
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ($output -join [Environment]::NewLine)
    }
}

try {
    $env:GITHUB_OUTPUT = ""
    foreach ($invalidTag in @("v0.1", "v0.1.1", "release-v0.1.0", "v0.1.0\n")) {
        $env:RELEASE_TAG = $invalidTag
        $output = & pwsh -NoProfile -File $tagScriptPath 2>&1
        $exitCode = $LASTEXITCODE
        Assert-Contract ($exitCode -ne 0) "Release tag validator accepted invalid tag '$invalidTag'. Output: $($output -join [Environment]::NewLine)"
    }

    $env:RELEASE_TAG = "v0.1.0"
    $output = & pwsh -NoProfile -File $tagScriptPath 2>&1
    Assert-Contract ($LASTEXITCODE -eq 0) "Release tag validator rejected v0.1.0."
    Assert-Contract (($output -join [Environment]::NewLine).Contains("version=0.1.0", [StringComparison]::Ordinal)) "Release tag validator did not emit version 0.1.0."

    $planned = Invoke-ChangelogContract @"
# Changelog

## [Unreleased]

## [0.1.0] - Planned (not yet published)
"@
    Assert-Contract ($planned.ExitCode -ne 0) "A planned release entry passed the changelog publication gate."

    $nestedUnreleased = Invoke-ChangelogContract @"
# Changelog

# [Unreleased]

## [0.1.0] - $today
"@
    Assert-Contract ($nestedUnreleased.ExitCode -ne 0) "A release entry nested under Unreleased passed the changelog publication gate."

    $bodyMarkerCases = @(
        [pscustomobject]@{ Name = "Planned"; Body = "Planned first public release notes." },
        [pscustomobject]@{ Name = "not yet published"; Body = "This release is not yet published." },
        [pscustomobject]@{ Name = "Unreleased"; Body = "Release notes remain Unreleased." },
        [pscustomobject]@{ Name = "TBD"; Body = "Release details are TBD." },
        [pscustomobject]@{ Name = "Draft"; Body = "This is a Draft release entry." }
    )
    foreach ($bodyMarkerCase in $bodyMarkerCases) {
        $bodyMarker = Invoke-ChangelogContract @"
# Changelog

## [Unreleased]

- Future work remains Unreleased and is TBD.

## [0.1.0] - $today

- $($bodyMarkerCase.Body)
"@
        Assert-Contract ($bodyMarker.ExitCode -ne 0) "A finalized release entry with a body-level $($bodyMarkerCase.Name) marker passed the changelog publication gate."
    }

    $readmePath = Join-Path $repositoryRoot "README.md"
    $originalReadme = [IO.File]::ReadAllText($readmePath)
    try {
        [IO.File]::WriteAllText($readmePath, @"
dotnet tool install --global KeelMatrix.FixtureVault \
  --version 0.1.0
"@, [Text.UTF8Encoding]::new($false))

        $finalized = Invoke-ChangelogContract @"
# Changelog

## [Unreleased]

- Future work remains Unreleased and is TBD.

## [0.1.0] - $today

### Added

- Finalized release notes.
"@
        Assert-Contract ($finalized.ExitCode -eq 0) "A finalized, internally consistent multiline install example was rejected: $($finalized.Output)"

        [IO.File]::WriteAllText($readmePath, @"
dotnet tool install --global KeelMatrix.FixtureVault `
  --version 0.1.1
"@, [Text.UTF8Encoding]::new($false))
        $multilineInstallMismatch = Invoke-ChangelogContract @"
# Changelog

## [Unreleased]

## [0.1.0] - $today

- Finalized release notes.
"@
        Assert-Contract ($multilineInstallMismatch.ExitCode -ne 0) "A multiline install-example/version mismatch passed the publication gate."
    }
    finally {
        [IO.File]::WriteAllText($readmePath, $originalReadme, [Text.UTF8Encoding]::new($false))
    }

    $trackedChangelogPath = Join-Path $repositoryRoot "CHANGELOG.md"
    $realContractParameters = @{
        ExpectedVersion = "0.1.0"
        ExpectedPackageVersion = "0.1.0"
        ExpectedCommit = $script:RepositoryCommit
        ChangelogPath = $trackedChangelogPath
        RepositoryRoot = $repositoryRoot
    }
    $realChangelogOutput = & pwsh -NoProfile -File $changelogScriptPath @realContractParameters 2>&1
    $realChangelogExitCode = $LASTEXITCODE
    $realChangelogText = [IO.File]::ReadAllText($trackedChangelogPath)
    $realChangelogIsPlanned = $realChangelogText -match '(?im)^##[ \t]+\[0\.1\.0\][^\r\n]*(?:planned|not[ \t-]+yet[ \t-]+published)'
    if ($realChangelogIsPlanned) {
        Assert-Contract ($realChangelogExitCode -ne 0) "The real planned CHANGELOG.md passed the changelog publication gate. Output: $($realChangelogOutput -join [Environment]::NewLine)"
    }
    else {
        Assert-Contract ($realChangelogExitCode -eq 0) "The finalized real CHANGELOG.md was rejected by the changelog publication gate. Output: $($realChangelogOutput -join [Environment]::NewLine)"
    }

    $changelogTagMismatch = Invoke-ChangelogContract @"
# Changelog

## [Unreleased]

## [0.1.0] - $today
"@ -Version "0.1.1" -PackageVersion "0.1.1"
    Assert-Contract ($changelogTagMismatch.ExitCode -ne 0) "A changelog/tag version mismatch passed the publication gate."

    $packageMismatch = Invoke-ChangelogContract @"
# Changelog

## [Unreleased]

## [0.1.0] - $today
"@ -PackageVersion "0.1.1"
    Assert-Contract ($packageMismatch.ExitCode -ne 0) "A changelog/package version mismatch passed the publication gate."

    $wrongCommit = ("0" * $script:RepositoryCommit.Length)
    $commitMismatch = Invoke-ChangelogContract @"
# Changelog

## [Unreleased]

## [0.1.0] - $today
"@ -Commit $wrongCommit
    Assert-Contract ($commitMismatch.ExitCode -ne 0) "A changelog contract check accepted a commit different from the checked-out commit."

    $originalChangelog = [IO.File]::ReadAllText($trackedChangelogPath)
    try {
        [IO.File]::WriteAllText($trackedChangelogPath, $originalChangelog + "`n", [Text.UTF8Encoding]::new($false))
        $trackedMismatch = & pwsh -NoProfile -File $changelogScriptPath `
            -ExpectedVersion "0.1.0" `
            -ExpectedPackageVersion "0.1.0" `
            -ExpectedCommit $script:RepositoryCommit `
            -RepositoryRoot $repositoryRoot 2>&1
        Assert-Contract ($LASTEXITCODE -ne 0) "A tracked changelog modified after the checked-out commit passed the publication gate."
    }
    finally {
        [IO.File]::WriteAllText($trackedChangelogPath, $originalChangelog, [Text.UTF8Encoding]::new($false))
    }
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }

    if ($null -eq $previousTag) {
        Remove-Item Env:RELEASE_TAG -ErrorAction SilentlyContinue
    }
    else {
        $env:RELEASE_TAG = $previousTag
    }

    if ($null -eq $previousOutput) {
        Remove-Item Env:GITHUB_OUTPUT -ErrorAction SilentlyContinue
    }
    else {
        $env:GITHUB_OUTPUT = $previousOutput
    }
}

Write-Host "Release contract passed: split validation/publication, exact tag validation, exact artifact set, and fail-closed publication rules."
