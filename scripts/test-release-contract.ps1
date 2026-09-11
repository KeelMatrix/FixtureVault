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

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$workflowPath = Join-Path $repositoryRoot ".github/workflows/release.yml"
$ciWorkflowPath = Join-Path $repositoryRoot ".github/workflows/ci.yml"
$tagScriptPath = Join-Path $repositoryRoot "scripts/validate-release-tag.ps1"
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
Assert-Contract (-not $validation.Contains("id-token: write", [StringComparison]::Ordinal)) "The release validation job must not request id-token: write."
Assert-Contract ($validation.Contains("dotnet restore", [StringComparison]::Ordinal)) "Release validation must restore the solution."
Assert-Contract ($validation.Contains("dotnet build", [StringComparison]::Ordinal)) "Release validation must build the solution."
Assert-Contract ($validation.Contains("dotnet test", [StringComparison]::Ordinal)) "Release validation must test the solution."
Assert-Contract ($validation.Contains("dotnet pack", [StringComparison]::Ordinal)) "Release validation must pack the tool."
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

$previousTag = $env:RELEASE_TAG
$previousOutput = $env:GITHUB_OUTPUT
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
}
finally {
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
