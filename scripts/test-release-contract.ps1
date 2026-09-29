[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')

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
$historyWorkflowPath = Join-Path $repositoryRoot ".github/workflows/history-hygiene.yml"
$historyGuardPath = Join-Path $repositoryRoot ".githooks/check-history"
$tagScriptPath = Join-Path $repositoryRoot "scripts/validate-release-tag.ps1"
$changelogScriptPath = Join-Path $repositoryRoot "scripts/test-changelog-contract.ps1"
$smokeScriptPath = Join-Path $repositoryRoot "scripts/package-consumer-smoke.ps1"
$auditScriptPath = Join-Path $repositoryRoot "scripts/audit-vulnerabilities.ps1"
$devGuidePath = Join-Path $repositoryRoot "docs/DEV.md"
$workflow = [IO.File]::ReadAllText($workflowPath)
$ciWorkflow = [IO.File]::ReadAllText($ciWorkflowPath)
$historyWorkflow = [IO.File]::ReadAllText($historyWorkflowPath)
$historyGuard = [IO.File]::ReadAllText($historyGuardPath)
$smokeScript = [IO.File]::ReadAllText($smokeScriptPath)
$auditScript = [IO.File]::ReadAllText($auditScriptPath)
$devGuide = [IO.File]::ReadAllText($devGuidePath)
$releaseContractSource = [IO.File]::ReadAllText($PSCommandPath)
$reservedReleaseTerms = @(
    ([char[]](0x66, 0x72, 0x6f, 0x6e, 0x74, 0x69, 0x65, 0x72) -join ""),
    (([char[]](0x70, 0x6c, 0x61, 0x6e, 0x6e, 0x65, 0x64) -join "") + ([char[]](0x50, 0x72, 0x65, 0x46, 0x72, 0x6f, 0x6e, 0x74, 0x69, 0x65, 0x72) -join "")),
    (([char[]](0x63, 0x6f, 0x6e, 0x74, 0x72, 0x61, 0x64, 0x69, 0x63, 0x74, 0x6f, 0x72, 0x79) -join "") + ([char[]](0x50, 0x72, 0x65, 0x46, 0x72, 0x6f, 0x6e, 0x74, 0x69, 0x65, 0x72) -join "")),
    ([char[]](0x6f, 0x72, 0x63, 0x68, 0x65, 0x73, 0x74, 0x72, 0x61, 0x74, 0x69, 0x6f, 0x6e) -join ""),
    (([char[]](0x6d, 0x6f, 0x64, 0x65, 0x6c) -join "") + "/" + ([char[]](0x61, 0x67, 0x65, 0x6e, 0x74) -join "") + " " + ([char[]](0x72, 0x65, 0x76, 0x69, 0x65, 0x77) -join "")),
    ([char[]](0x70, 0x61, 0x70, 0x65, 0x72, 0x63, 0x6c, 0x69, 0x70) -join ""),
    ([char[]](0x63, 0x6f, 0x64, 0x65, 0x78) -join ""),
    ([char[]](0x63, 0x68, 0x61, 0x74, 0x67, 0x70, 0x74) -join ""),
    ([char[]](0x6f, 0x70, 0x65, 0x6e, 0x61, 0x69) -join ""),
    ([char[]](0x63, 0x6c, 0x61, 0x75, 0x64, 0x65) -join ""),
    ([char[]](0x63, 0x6f, 0x70, 0x69, 0x6c, 0x6f, 0x74) -join ""),
    ([char[]](0x67, 0x65, 0x6d, 0x69, 0x6e, 0x69) -join ""),
    ([char[]](0x61, 0x6e, 0x74, 0x68, 0x72, 0x6f, 0x70, 0x69, 0x63) -join ""),
    ([char[]](0x6f, 0x72, 0x63, 0x68, 0x65, 0x73, 0x74, 0x72, 0x61, 0x74, 0x6f, 0x72) -join ""),
    ([char[]](0x61, 0x67, 0x65, 0x6e, 0x74) -join ""),
    ([char[]](0x6d, 0x6f, 0x64, 0x65, 0x6c) -join ""),
    ([char[]](0x70, 0x72, 0x6f, 0x6d, 0x70, 0x74) -join ""),
    (([char[]](0x72, 0x65, 0x76, 0x69, 0x65, 0x77) -join "") + " " + ([char[]](0x72, 0x6f, 0x75, 0x6e, 0x64) -join "")),
    (([char[]](0x72, 0x65, 0x76, 0x69, 0x65, 0x77) -join "") + " " + ([char[]](0x70, 0x61, 0x73, 0x73) -join "")),
    (([char[]](0x61, 0x63, 0x63, 0x65, 0x70, 0x74, 0x61, 0x6e, 0x63, 0x65) -join "") + " " + ([char[]](0x72, 0x6f, 0x75, 0x6e, 0x64) -join "")),
    (([char[]](0x61, 0x63, 0x63, 0x65, 0x70, 0x74, 0x61, 0x6e, 0x63, 0x65) -join "") + " " + ([char[]](0x70, 0x61, 0x73, 0x73) -join "")),
    (([char[]](0x67, 0x65, 0x6e, 0x65, 0x72, 0x61, 0x74, 0x65, 0x64) -join "") + " " + ([char[]](0x62, 0x79) -join ""))
)
foreach ($reservedReleaseTerm in $reservedReleaseTerms) {
    Assert-Contract (-not $releaseContractSource.Contains($reservedReleaseTerm, [StringComparison]::OrdinalIgnoreCase)) "Release contract source contains reserved wording."
}

# The two guard-only files below contain negative test patterns by design. They
# are not release content and must never be packed or echoed by validation.
$guardOnlyReleaseFiles = @(
    ".githooks/commit-msg",
    "scripts/test-history-gate.ps1"
)
$reservedReleasePattern = "(?i)(?<![a-z0-9])(" + (($reservedReleaseTerms | ForEach-Object { [Text.RegularExpressions.Regex]::Escape($_) }) -join "|") + ")(?![a-z0-9])"
$binaryExtensions = @(".png", ".gif", ".jpg", ".jpeg", ".ico", ".bmp", ".pdf", ".zip", ".nupkg", ".snupkg", ".dll", ".pdb")
$trackedReleaseFiles = @(& git -C $repositoryRoot ls-files)
foreach ($trackedReleaseFile in $trackedReleaseFiles) {
    if ($guardOnlyReleaseFiles -contains $trackedReleaseFile) {
        continue
    }

    $extension = [IO.Path]::GetExtension($trackedReleaseFile).ToLowerInvariant()
    if ($binaryExtensions -contains $extension) {
        continue
    }

    $trackedReleasePath = Join-Path $repositoryRoot $trackedReleaseFile
    if (-not (Test-Path -LiteralPath $trackedReleasePath -PathType Leaf)) {
        continue
    }

    try {
        $trackedReleaseText = [IO.File]::ReadAllText($trackedReleasePath)
    }
    catch [IO.IOException] {
        continue
    }

    Assert-Contract (-not [Text.RegularExpressions.Regex]::IsMatch($trackedReleaseText, $reservedReleasePattern)) "Release-facing file contains reserved wording: $trackedReleaseFile."
}

function Test-PlannedFirstReleaseShape {
    param(
        [Parameter(Mandatory = $true)][string]$Content
    )

    $plannedHeading = [Text.RegularExpressions.Regex]::Match(
        $Content,
        '(?im)^##[ \t]+\[0\.1\.0\][ \t]+-[ \t]+planned[ \t]+\(not[ \t-]+yet[ \t-]+published\)[ \t]*(?:\r)?$')
    if (-not $plannedHeading.Success) {
        return $false
    }

    $unreleasedSection = [Text.RegularExpressions.Regex]::Match(
        $Content,
        '(?ims)^##[ \t]+\[unreleased\](?<body>.*?)(?=^##[ \t]+\[0\.1\.0\])')
    if (-not $unreleasedSection.Success -or $unreleasedSection.Groups["body"].Value -match '(?im)^###[ \t]+fixed\b') {
        return $false
    }

    $plannedSection = $Content.Substring($plannedHeading.Index)
    return ($plannedSection -match '(?im)^###[ \t]+added\b') -and
        ($plannedSection -notmatch '(?im)^###[ \t]+fixed\b')
}

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
Assert-Contract ($validation.Contains("fetch-depth: 0", [StringComparison]::Ordinal)) "Release validation must check out complete non-shallow history for repository-wide gates."
Assert-Contract ($validation.Contains("sh .githooks/check-history", [StringComparison]::Ordinal)) "Release validation must enforce the non-shallow commit-history gate over every reachable commit."
Assert-Contract ($validation.Contains("dotnet build", [StringComparison]::Ordinal)) "Release validation must build the solution."
Assert-Contract ($validation.Contains("dotnet test", [StringComparison]::Ordinal)) "Release validation must test the solution."
Assert-Contract ($validation.Contains("dotnet pack", [StringComparison]::Ordinal)) "Release validation must pack the tool."
Assert-Contract ($validation.Contains("test-changelog-contract.ps1", [StringComparison]::Ordinal)) "Release validation must run the changelog/version contract."
Assert-Contract ($validation.IndexOf("test-changelog-contract.ps1", [StringComparison]::Ordinal) -lt $validation.IndexOf("dotnet pack", [StringComparison]::Ordinal)) "The changelog/version contract must run before release packing."
Assert-Contract ($validation.Contains('EXPECTED_COMMIT: ${{ github.sha }}', [StringComparison]::Ordinal)) "Release validation must bind changelog checks to the checked-out commit."
Assert-Contract ($validation.Contains('-ExpectedPackageVersion', [StringComparison]::Ordinal)) "Release validation must pass the expected package version to the changelog contract."
Assert-Contract ($validation.Contains('-ExpectedCommit $env:EXPECTED_COMMIT', [StringComparison]::Ordinal)) "Release validation must pass the expected commit to the changelog contract."
Assert-Contract ($validation.Contains("inspect-package.ps1", [StringComparison]::Ordinal)) "Release validation must inspect the package archives."
Assert-Contract ($validation.Contains('$expectedCommit = (git rev-parse HEAD).Trim()', [StringComparison]::Ordinal)) "Release package inspection must resolve the checked-out commit."
Assert-Contract ($validation.Contains('-ExpectedCommit $expectedCommit', [StringComparison]::Ordinal)) "Release package inspection must validate exact repository provenance."
Assert-Contract ($validation.Contains("package-consumer-smoke.ps1", [StringComparison]::Ordinal)) "Release validation must run the package consumer smoke."
Assert-Contract ($validation.Contains("audit-vulnerabilities.ps1", [StringComparison]::Ordinal)) "Release validation must run the repository vulnerability audit."
Assert-Contract ($ciWorkflow.Contains(".githooks/check-history", [StringComparison]::Ordinal)) "Ordinary CI must enforce the same non-shallow complete-history guard as release validation."
Assert-Contract ($ciWorkflow.Contains("fetch-depth: 0", [StringComparison]::Ordinal)) "Ordinary CI must provide complete non-shallow history to the history guard."
Assert-Contract ($historyWorkflow.Contains(".githooks/check-history", [StringComparison]::Ordinal)) "History hygiene must enforce the same non-shallow complete-history guard as ordinary CI and release validation."
Assert-Contract ($historyWorkflow.Contains("fetch-depth: 0", [StringComparison]::Ordinal)) "History hygiene must provide complete non-shallow history to the history guard."
Assert-Contract ($historyGuard.Contains("git rev-parse --is-shallow-repository", [StringComparison]::Ordinal)) "The history guard must detect shallow state from Git itself."
Assert-Contract ($historyGuard.Contains("HISTORY_GUARD=FAIL reason=shallow-history", [StringComparison]::Ordinal)) "The history guard must report an honest shallow-history failure."
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
Assert-Contract ($auditScript.Contains('"--format",', [StringComparison]::Ordinal) -and $auditScript.Contains('"json",', [StringComparison]::Ordinal)) "The vulnerability audit must request structured JSON output."
Assert-Contract ($auditScript.Contains('"--output-version",', [StringComparison]::Ordinal) -and $auditScript.Contains('"1"', [StringComparison]::Ordinal)) "The vulnerability audit must pin its structured output version."
Assert-Contract ($auditScript.Contains('Get-ExpectedProjectPaths', [StringComparison]::Ordinal) -and $auditScript.Contains('$seenProjects', [StringComparison]::Ordinal)) "The vulnerability audit must reconcile complete solution project coverage."
Assert-Contract (Test-Path -LiteralPath $changelogScriptPath -PathType Leaf) "The changelog/version contract script is missing."
Assert-Contract (Test-Path -LiteralPath $smokeScriptPath -PathType Leaf) "The package consumer smoke script is missing."
Assert-Contract ($smokeScript.Contains("Assert-TelemetrySuppressed", [StringComparison]::Ordinal)) "Package consumer smoke must guard every child tool invocation with telemetry suppression."
Assert-Contract ($smokeScript.Contains('[Environment]::SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", "1", "Process")', [StringComparison]::Ordinal)) "Package consumer smoke must establish process telemetry suppression itself."
Assert-Contract ($smokeScript.Contains('$originalTelemetryOptOut', [StringComparison]::Ordinal)) "Package consumer smoke must restore the caller telemetry setting."
Assert-Contract ($devGuide.Contains('$env:KEELMATRIX_NO_TELEMETRY = "1"', [StringComparison]::Ordinal)) "The developer guide must make source and package smoke commands safe when copied verbatim."
Assert-Contract ($devGuide.IndexOf('$env:KEELMATRIX_NO_TELEMETRY = "1"', [StringComparison]::Ordinal) -lt $devGuide.IndexOf('package-consumer-smoke.ps1', [StringComparison]::Ordinal)) "The developer guide must set telemetry suppression before the package smoke command."
Assert-AuditBeforePack "Normal CI" $ciWorkflow
Assert-AuditBeforePack "Release validation" $workflow
Assert-Contract ($ciWorkflow.Contains('git rev-parse HEAD', [StringComparison]::Ordinal) -and $ciWorkflow.Contains('-ExpectedCommit $expectedCommit', [StringComparison]::Ordinal)) "CI package inspection must validate exact repository provenance."

$previousTag = $env:RELEASE_TAG
$previousOutput = $env:GITHUB_OUTPUT
$script:RepositoryCommit = (& git -C $repositoryRoot rev-parse HEAD 2>&1 | Out-String).Trim()
$gitExitCode = $LASTEXITCODE
Assert-Contract ($gitExitCode -eq 0 -and $script:RepositoryCommit -match '^[0-9a-fA-F]{40,64}$') "Could not resolve the repository commit for changelog contract tests."
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("fixturevault-changelog-contract-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
$fixtureChangelogPath = Join-Path $fixtureRoot "CHANGELOG.md"
$contractRepositoryRoot = Join-Path $fixtureRoot "repository"
$today = [DateTime]::UtcNow.ToString("yyyy-MM-dd")

# Keep child output on separate raw streams. PowerShell's 2>&1 conversion renders
# ErrorRecords using the host width, which can split assertion fragments on CI.
function Invoke-PwshScript {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Arguments
    )

    $processArguments = @(
        "-NoProfile",
        "-File",
        $ScriptPath
    ) + $Arguments
    $result = Invoke-NestedProcess -Executable "pwsh" -ArgumentList $processArguments
    [pscustomobject]@{
        ExitCode = $result.ExitCode
        StandardOutput = $result.StandardOutput
        StandardError = $result.StandardError
        Output = $result.StandardOutput + $result.StandardError
    }
}

function Invoke-ChangelogContract {
    param(
        [string]$Content,
        [string]$Version = "0.1.0",
        [string]$PackageVersion = "0.1.0",
        [string]$Commit = $script:RepositoryCommit
    )

    [IO.File]::WriteAllText($fixtureChangelogPath, $Content, [Text.UTF8Encoding]::new($false))
    Invoke-PwshScript -ScriptPath $changelogScriptPath -Arguments @(
        "-ExpectedVersion", $Version,
        "-ChangelogPath", $fixtureChangelogPath,
        "-ExpectedPackageVersion", $PackageVersion,
        "-ExpectedCommit", $Commit,
        "-RepositoryRoot", $contractRepositoryRoot
    )
}

function Assert-ChangelogCase {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Content,
        [switch]$ShouldPass
    )

    $result = Invoke-ChangelogContract $Content
    $passed = if ($ShouldPass) {
        $result.ExitCode -eq 0
    }
    else {
        $result.ExitCode -ne 0
    }

    $expectedOutcome = if ($ShouldPass) { "pass" } else { "fail" }
    Write-Host "Changelog case '$Name': expected $expectedOutcome, exit code $($result.ExitCode)."
    Assert-Contract $passed "Changelog case '$Name' had the wrong exit code. Output: $($result.Output)"
}

try {
    # All changelog/README probes below intentionally run in an isolated clone
    # of the exact candidate commit. The probes mutate their inputs to verify
    # fail-closed behavior; mutating the active worktree would let concurrent
    # runs observe synthetic versions and could leave tracked files modified
    # when a process is interrupted.
    & git clone --quiet --local --no-hardlinks --no-checkout $repositoryRoot $contractRepositoryRoot 2>&1 | Out-Null
    $gitExitCode = $LASTEXITCODE
    Assert-Contract ($gitExitCode -eq 0) "Could not create the isolated changelog contract repository."
    & git -C $contractRepositoryRoot checkout --quiet --detach $script:RepositoryCommit 2>&1 | Out-Null
    $gitExitCode = $LASTEXITCODE
    Assert-Contract ($gitExitCode -eq 0) "Could not check out the isolated changelog contract repository."

    $env:GITHUB_OUTPUT = ""
    foreach ($invalidTag in @("v0.1", "v0.1.1", "release-v0.1.0", "v0.1.0\n")) {
        $env:RELEASE_TAG = $invalidTag
        $tagResult = Invoke-PwshScript -ScriptPath $tagScriptPath -Arguments @()
        Assert-Contract ($tagResult.ExitCode -ne 0) "Release tag validator accepted invalid tag '$invalidTag'. Output: $($tagResult.Output)"
    }

    $env:RELEASE_TAG = "v0.1.0"
    $tagResult = Invoke-PwshScript -ScriptPath $tagScriptPath -Arguments @()
    Assert-Contract ($tagResult.ExitCode -eq 0) "Release tag validator rejected v0.1.0."
    Assert-Contract ($tagResult.StandardOutput.Contains("version=0.1.0", [StringComparison]::Ordinal)) "Release tag validator did not emit version 0.1.0."

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

    $nestedAncestorUnreleased = Invoke-ChangelogContract @"
# Changelog

# [Unreleased]

## Release train

### [0.1.0] - $today

- Finalized release notes.
"@
    Assert-Contract ($nestedAncestorUnreleased.ExitCode -ne 0) "A release entry with an Unreleased ancestor above an intermediate heading passed the changelog publication gate."

    $cleanNestedRelease = Invoke-ChangelogContract @"
# Changelog

# Release history

## Release train

### [0.1.0] - $today

#### Added

- Finalized release notes.
"@
    Assert-Contract ($cleanNestedRelease.ExitCode -eq 0) "A finalized release entry in a clean nested heading structure was rejected: $($cleanNestedRelease.Output)"

    $deepAncestorUnreleased = Invoke-ChangelogContract @"
# Changelog

## [Unreleased]

### Release train

#### [0.1.0] - $today

- Finalized release notes.
"@
    Assert-Contract ($deepAncestorUnreleased.ExitCode -ne 0) "A release entry nested more than one level below Unreleased passed the changelog publication gate."

    $splitWhitespaceCases = @(
        [pscustomobject]@{ Name = "LF"; Body = "This release is not`nyet published." },
        [pscustomobject]@{ Name = "CRLF"; Body = "This release is not`r`nyet published." },
        [pscustomobject]@{ Name = "blank line"; Body = "This release is not`n`n`nyet published." },
        [pscustomobject]@{ Name = "Markdown hard break"; Body = "This release is not  `nyet published." },
        [pscustomobject]@{ Name = "tab and newline mixture"; Body = "This release is not`t`nyet`t published." },
        [pscustomobject]@{ Name = "non-breaking spaces"; Body = "This release is not$([char]0x00a0)yet$([char]0x00a0)published." },
        [pscustomobject]@{ Name = "case-insensitive LF"; Body = "this release is NOT`n`nyet PUBLISHED." },
        [pscustomobject]@{ Name = "equivalent wording"; Body = "This release is not`n`nready." }
    )
    foreach ($splitWhitespaceCase in $splitWhitespaceCases) {
        $splitMarker = Invoke-ChangelogContract @"
# Changelog

## [Unreleased]

## [0.1.0] - $today

- $($splitWhitespaceCase.Body)
"@
        Assert-Contract ($splitMarker.ExitCode -ne 0) "A release marker split by $($splitWhitespaceCase.Name) passed the changelog publication gate."
    }

    $markedUpSplitCases = @(
        [pscustomobject]@{ Name = "list items"; Body = "- Release is not`n- yet published." },
        [pscustomobject]@{ Name = "ordered list items"; Body = "1. Release is not`n2. yet published." },
        [pscustomobject]@{ Name = "blockquote lines"; Body = "> Release is not`n> yet published." },
        [pscustomobject]@{ Name = "emphasis markers"; Body = "**Release** is **not**`n`nyet **published**." }
    )
    foreach ($markedUpSplitCase in $markedUpSplitCases) {
        $markedUpSplit = Invoke-ChangelogContract @"
# Changelog

## [Unreleased]

## [0.1.0] - $today

$($markedUpSplitCase.Body)
"@
        Assert-Contract ($markedUpSplit.ExitCode -ne 0) "A release marker split across $($markedUpSplitCase.Name) passed the changelog publication gate."
    }

    $ancestorBodySplitCases = @(
        [pscustomobject]@{ Name = "ancestor heading/body LF"; Body = "# Release is not`n`nyet published" },
        [pscustomobject]@{ Name = "ancestor heading/body CRLF"; Body = "# Release is not`r`n`r`nyet published" },
        [pscustomobject]@{ Name = "ancestor list items"; Body = "# Release is not`n`n- yet`n- published" },
        [pscustomobject]@{ Name = "ancestor blockquote lines"; Body = "# Release is not`n`n> yet`n> published" }
    )
    foreach ($ancestorBodySplitCase in $ancestorBodySplitCases) {
        $ancestorBodySplit = Invoke-ChangelogContract @"
# Changelog

$($ancestorBodySplitCase.Body)

## Release train

### [0.1.0] - $today

#### Added

- Finalized release notes.
"@
        Assert-Contract ($ancestorBodySplit.ExitCode -ne 0) "A pre-release marker split across the $($ancestorBodySplitCase.Name) passed the changelog publication gate."
    }

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

    $statementMarkerCases = @(
        [pscustomobject]@{ Name = "bare Planned line"; Body = "Planned" },
        [pscustomobject]@{ Name = "bare Unreleased line"; Body = "Unreleased" },
        [pscustomobject]@{ Name = "bare TBD line"; Body = "TBD" },
        [pscustomobject]@{ Name = "bare Draft line"; Body = "Draft" },
        [pscustomobject]@{ Name = "bare Pending line"; Body = "Pending" },
        [pscustomobject]@{ Name = "status label"; Body = "Status: Draft" },
        [pscustomobject]@{ Name = "release state label"; Body = "Release state: TBD" },
        [pscustomobject]@{ Name = "bullet marker"; Body = "### Notes`n`n- TBD" }
    )
    foreach ($statementMarkerCase in $statementMarkerCases) {
        $statementMarker = Invoke-ChangelogContract @"
# Changelog

## [Unreleased]

## [0.1.0] - $today

$($statementMarkerCase.Body)
"@
        Assert-Contract ($statementMarker.ExitCode -ne 0) "A finalized release entry with a $($statementMarkerCase.Name) pre-release marker passed the changelog publication gate."
    }

    $ordinaryProse = Invoke-ChangelogContract @"
# Changelog

## [Unreleased]

## [0.1.0] - $today

### Added

- The Draft API type is documented and tested.
"@
    Assert-Contract ($ordinaryProse.ExitCode -eq 0) "Ordinary prose describing a Draft API type was treated as a release marker: $($ordinaryProse.Output)"

    $ordinaryProseWithDistantReleaseContext = Invoke-ChangelogContract @"
# Changelog

## [Unreleased]

## [0.1.0] - $today

### Added

- The Draft API type is documented for a future release.
"@
    Assert-Contract ($ordinaryProseWithDistantReleaseContext.ExitCode -eq 0) "Unrelated Draft API prose with a distant release reference was treated as a release marker: $($ordinaryProseWithDistantReleaseContext.Output)"

    $boundaryProseBodies = @(
        "- Added prerelease handling and tests.",
        "- Tracks work in progress files during scanning.",
        "- Lists features to be released in a later version."
    )
    foreach ($boundaryProseBody in $boundaryProseBodies) {
        $boundaryProse = Invoke-ChangelogContract @"
# Changelog

## [Unreleased]

## [0.1.0] - $today

### Added

$boundaryProseBody
"@
        Assert-Contract ($boundaryProse.ExitCode -eq 0) "Ordinary prose '$boundaryProseBody' was treated as a release marker: $($boundaryProse.Output)"
    }

    $markerAfterTargetBoundary = Invoke-ChangelogContract @"
# Changelog

## [0.1.0] - $today

### Added

- Finalized release notes.

## Future releases [Unreleased]

- TBD.
"@
    Assert-Contract ($markerAfterTargetBoundary.ExitCode -eq 0) "A pre-release marker after the target section boundary changed the target result: $($markerAfterTargetBoundary.Output)"

    $firstReleaseHeadingCases = @(
        [pscustomobject]@{ Name = "first release rejects Changed heading"; Body = "### Changed`n`n- Describes a release difference." },
        [pscustomobject]@{ Name = "first release rejects Fixed heading"; Body = "### Fixed`n`n- Describes a release difference." },
        [pscustomobject]@{ Name = "first release rejects Deprecated heading"; Body = "### Deprecated`n`n- Describes a release difference." },
        [pscustomobject]@{ Name = "first release rejects Removed heading"; Body = "### Removed`n`n- Describes a release difference." },
        [pscustomobject]@{ Name = "first release rejects Security heading"; Body = "### Security`n`n- Describes a release difference." },
        [pscustomobject]@{ Name = "first release rejects Improved heading"; Body = "### Improved`n`n- Describes a release difference." },
        [pscustomobject]@{ Name = "first release rejects Notes heading"; Body = "### Notes`n`n- Describes a release difference." },
        [pscustomobject]@{ Name = "first release rejects decorated Changed heading"; Body = "### **Changed**`n`n- Describes a release difference." },
        [pscustomobject]@{ Name = "first release rejects uppercase CHANGED heading"; Body = "### CHANGED`n`n- Describes a release difference." },
        [pscustomobject]@{ Name = "first release rejects missing Added heading"; Body = "- A bare release bullet is not categorized." },
        [pscustomobject]@{ Name = "first release rejects Added plus Changed headings"; Body = "### Added`n`n- Provides the initial capability.`n`n### Changed`n`n- Describes a release difference." }
    )
    foreach ($headingCase in $firstReleaseHeadingCases) {
        Assert-ChangelogCase -Name $headingCase.Name -Content @"
# Changelog

## [0.1.0] - $today

$($headingCase.Body)
"@
    }

    $firstReleaseBannedWordingCases = @(
        [pscustomobject]@{ Name = "first release rejects wording now"; Phrase = "now" },
        [pscustomobject]@{ Name = "first release rejects wording no longer"; Phrase = "no longer" },
        [pscustomobject]@{ Name = "first release rejects wording previously"; Phrase = "previously" },
        [pscustomobject]@{ Name = "first release rejects wording formerly"; Phrase = "formerly" },
        [pscustomobject]@{ Name = "first release rejects wording used to"; Phrase = "used to" },
        [pscustomobject]@{ Name = "first release rejects wording fixed"; Phrase = "fixed" },
        [pscustomobject]@{ Name = "first release rejects wording fixes"; Phrase = "fixes" },
        [pscustomobject]@{ Name = "first release rejects wording corrected"; Phrase = "corrected" },
        [pscustomobject]@{ Name = "first release rejects wording resolved"; Phrase = "resolved" },
        [pscustomobject]@{ Name = "first release rejects wording addressed"; Phrase = "addressed" },
        [pscustomobject]@{ Name = "first release rejects wording this removes"; Phrase = "this removes" },
        [pscustomobject]@{ Name = "first release rejects wording this fixes"; Phrase = "this fixes" },
        [pscustomobject]@{ Name = "first release rejects wording changed from"; Phrase = "changed from" }
    )
    foreach ($wordingCase in $firstReleaseBannedWordingCases) {
        Assert-ChangelogCase -Name $wordingCase.Name -Content @"
# Changelog

## [0.1.0] - $today

### Added

- The initial release $($wordingCase.Phrase) provides the documented capability.
"@
    }

    Assert-ChangelogCase -Name "first release accepts valid Added-only entry" -ShouldPass -Content @"
# Changelog

## [0.1.0] - $today

### Added

- Provides read-only snapshot and golden-file auditing with stable reports.
"@

    Assert-ChangelogCase -Name "first release accepts whole-token near misses" -ShouldPass -Content @"
# Changelog

## [0.1.0] - $today

### Added

- Documents known and unknown behavior for renewed inputs found nowhere else, including fixedness and prefixes controls.
"@

    Assert-ChangelogCase -Name "later release permits Changed and transition wording" -ShouldPass -Content @"
# Changelog

## [0.0.9] - $today

### Added

- Provides the initial capability.

## [0.1.0] - $today

### Changed

- The scanner now supports the updated release behavior.
"@

    $invalidSemVerHeadingCases = @(
        [pscustomobject]@{ Name = "invalid lower release rejects leading-zero patch"; Version = "0.0.09"; ReasonFragment = "leading zero" },
        [pscustomobject]@{ Name = "invalid lower release rejects leading-zero major"; Version = "00.0.9"; ReasonFragment = "leading zero" },
        [pscustomobject]@{ Name = "invalid lower release rejects leading-zero minor"; Version = "0.00.9"; ReasonFragment = "leading zero" },
        [pscustomobject]@{ Name = "invalid lower release rejects leading-zero numeric prerelease"; Version = "0.0.9-01"; ReasonFragment = "numeric pre-release" }
    )
    foreach ($invalidSemVerHeadingCase in $invalidSemVerHeadingCases) {
        $invalidSemVer = Invoke-ChangelogContract @"
# Changelog

## [$($invalidSemVerHeadingCase.Version)] - $today

### Added

- Malformed lower-looking release.

## [0.1.0] - $today

### Changed

- This transition section must be rejected.
"@
        Write-Host "Changelog case '$($invalidSemVerHeadingCase.Name)': expected fail, exit code $($invalidSemVer.ExitCode)."
        Assert-Contract ($invalidSemVer.ExitCode -ne 0) "Changelog case '$($invalidSemVerHeadingCase.Name)' unexpectedly passed."
        Assert-Contract ($invalidSemVer.StandardOutput.Contains("Changelog/version contract failed: Release heading", [StringComparison]::Ordinal) -and
            $invalidSemVer.StandardOutput.Contains($invalidSemVerHeadingCase.Version, [StringComparison]::Ordinal) -and
            $invalidSemVer.StandardOutput.Contains("invalid SemVer", [StringComparison]::Ordinal) -and
            $invalidSemVer.StandardOutput.Contains($invalidSemVerHeadingCase.ReasonFragment, [StringComparison]::Ordinal)) "Changelog case '$($invalidSemVerHeadingCase.Name)' did not report the malformed heading and reason on the stable output channel: $($invalidSemVer.Output)"
    }

    Assert-ChangelogCase -Name "valid prerelease release heading parses" -ShouldPass -Content @"
# Changelog

## [0.1.0-rc.1] - $today

### Added

- Provides the prerelease capability.

## [0.1.0] - $today

### Changed

- The scanner now supports the stable release behavior.
"@

    $benchmarkTokenCount = 100000
    $benchmarkBody = ("benign " * $benchmarkTokenCount) -join ""
    $benchmarkContent = @"
# Changelog

## [0.1.0] - $today

### Added

- $benchmarkBody
"@
    $benchmarkTimer = [Diagnostics.Stopwatch]::StartNew()
    $benchmark = Invoke-ChangelogContract $benchmarkContent
    $benchmarkTimer.Stop()
    $benchmarkNormalizedTokenCount = $benchmarkTokenCount + 7
    Assert-Contract ($benchmark.ExitCode -eq 0) "The 100,000-token boundedness benchmark failed: $($benchmark.Output)"
    $benchmarkSeconds = $benchmarkTimer.Elapsed.TotalSeconds.ToString("F3", [Globalization.CultureInfo]::InvariantCulture)
    Write-Host "Boundedness benchmark: $benchmarkNormalizedTokenCount normalized target-section tokens; command: pwsh -NoProfile -File ./scripts/test-release-contract.ps1; validator elapsed seconds: $benchmarkSeconds."

    $budgetTokenCount = 200001
    $budgetBody = ("benign " * $budgetTokenCount) -join ""
    $budgetCase = Invoke-ChangelogContract @"
# Changelog

## [0.1.0] - $today

### Added

- $budgetBody
"@
    Write-Host "Changelog case 'target section token budget rejects over-budget input': expected fail, exit code $($budgetCase.ExitCode)."
    Assert-Contract ($budgetCase.ExitCode -ne 0 -and
        $budgetCase.StandardOutput.Contains("normalized tokens", [StringComparison]::Ordinal) -and
        $budgetCase.StandardOutput.Contains("fixed", [StringComparison]::Ordinal) -and
        $budgetCase.StandardOutput.Contains("200000", [StringComparison]::Ordinal) -and
        $budgetCase.StandardOutput.Contains("200008", [StringComparison]::Ordinal)) "The target section token budget case did not fail with the expected budget and observed count on the stable output channel: $($budgetCase.Output)"

    $readmePath = Join-Path $contractRepositoryRoot "README.md"
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

        [IO.File]::WriteAllText($readmePath, @"
dotnet tool install --global KeelMatrix.FixtureVault --version=0.1.0
"@, [Text.UTF8Encoding]::new($false))
        $equalsInstallConsistent = Invoke-ChangelogContract @"
# Changelog

## [Unreleased]

## [0.1.0] - $today

### Added

- Finalized release notes.
"@
        Assert-Contract ($equalsInstallConsistent.ExitCode -eq 0) "An equals-form install example with the release version was rejected: $($equalsInstallConsistent.Output)"

        [IO.File]::WriteAllText($readmePath, @"
dotnet tool install --global KeelMatrix.FixtureVault --version=0.2.0
"@, [Text.UTF8Encoding]::new($false))
        $singleLineEqualsMismatch = Invoke-ChangelogContract @"
# Changelog

## [Unreleased]

## [0.1.0] - $today

- Finalized release notes.
"@
        Assert-Contract ($singleLineEqualsMismatch.ExitCode -ne 0) "A single-line equals-form install-example/version mismatch passed the publication gate."

        [IO.File]::WriteAllText($readmePath, @"
dotnet tool install --global KeelMatrix.FixtureVault \
  --version=0.2.0
"@, [Text.UTF8Encoding]::new($false))
        $continuationEqualsMismatch = Invoke-ChangelogContract @"
# Changelog

## [Unreleased]

## [0.1.0] - $today

- Finalized release notes.
"@
        Assert-Contract ($continuationEqualsMismatch.ExitCode -ne 0) "A continuation-line equals-form install-example/version mismatch passed the publication gate."

        foreach ($quote in @([char]34, [char]39, [char]96)) {
            $quotedVersion = [string]$quote + "0.2.0" + [string]$quote
            [IO.File]::WriteAllText($readmePath, "dotnet tool install --global KeelMatrix.FixtureVault --version $quotedVersion`n", [Text.UTF8Encoding]::new($false))
            $quotedInstallMismatch = Invoke-ChangelogContract @"
# Changelog

## [Unreleased]

## [0.1.0] - $today

- Finalized release notes.
"@
            Assert-Contract ($quotedInstallMismatch.ExitCode -ne 0) "A quoted install-example/version mismatch passed the publication gate."
        }
    }
    finally {
        [IO.File]::WriteAllText($readmePath, $originalReadme, [Text.UTF8Encoding]::new($false))
    }

    $projectReadmePath = Join-Path $contractRepositoryRoot "src/KeelMatrix.FixtureVault/README.md"
    Assert-Contract (Test-Path -LiteralPath $projectReadmePath -PathType Leaf) "The project-local README is required for the version-consistency contract test."
    $originalProjectReadme = [IO.File]::ReadAllText($projectReadmePath)
    try {
        [IO.File]::WriteAllText($projectReadmePath, @"
dotnet tool install --global KeelMatrix.FixtureVault --version 0.2.0
"@, [Text.UTF8Encoding]::new($false))
        $projectReadmeInstallMismatch = Invoke-ChangelogContract @"
# Changelog

## [Unreleased]

## [0.1.0] - $today

- Finalized release notes.
"@
        Assert-Contract ($projectReadmeInstallMismatch.ExitCode -ne 0) "A project-local README install-example/version mismatch passed the publication gate."
    }
    finally {
        [IO.File]::WriteAllText($projectReadmePath, $originalProjectReadme, [Text.UTF8Encoding]::new($false))
    }

    $trackedChangelogPath = Join-Path $contractRepositoryRoot "CHANGELOG.md"
    $realContractParameters = @{
        ExpectedVersion = "0.1.0"
        ExpectedPackageVersion = "0.1.0"
        ExpectedCommit = $script:RepositoryCommit
        ChangelogPath = $trackedChangelogPath
        RepositoryRoot = $contractRepositoryRoot
    }
    $realChangelogResult = Invoke-PwshScript -ScriptPath $changelogScriptPath -Arguments @(
        "-ExpectedVersion", $realContractParameters.ExpectedVersion,
        "-ExpectedPackageVersion", $realContractParameters.ExpectedPackageVersion,
        "-ExpectedCommit", $realContractParameters.ExpectedCommit,
        "-ChangelogPath", $realContractParameters.ChangelogPath,
        "-RepositoryRoot", $realContractParameters.RepositoryRoot
    )
    $realChangelogOutput = $realChangelogResult.Output
    $realChangelogExitCode = $realChangelogResult.ExitCode
    $realChangelogText = [IO.File]::ReadAllText($trackedChangelogPath)
    $realChangelogIsPlanned = $realChangelogText -match '(?im)^##[ \t]+\[0\.1\.0\][^\r\n]*(?:planned|not[ \t-]+yet[ \t-]+published)'
    if ($realChangelogIsPlanned) {
        Assert-Contract (Test-PlannedFirstReleaseShape $realChangelogText) "The real planned CHANGELOG.md is not a coherent planned first-release state."
        Assert-Contract ($realChangelogExitCode -ne 0) "The real planned CHANGELOG.md passed the changelog publication gate. Output: $($realChangelogOutput -join [Environment]::NewLine)"
    }
    else {
        Assert-Contract ($realChangelogExitCode -eq 0) "The finalized real CHANGELOG.md was rejected by the changelog publication gate. Output: $($realChangelogOutput -join [Environment]::NewLine)"
    }

    $plannedFirstRelease = @"
# Changelog

## [Unreleased]

## [0.1.0] - Planned (not yet published)

### Added

- The first public tool contract is documented.
"@
    $plannedHeadingMatch = $plannedFirstRelease -match '(?im)^##[ \t]+\[0\.1\.0\][ \t]+-[ \t]+planned[ \t]+\(not[ \t-]+yet[ \t-]+published\)[ \t]*(?:\r)?$'
    $plannedAddedMatch = $plannedFirstRelease -match '(?im)^###[ \t]+added\b'
    $plannedNoFixedMatch = $plannedFirstRelease -notmatch '(?im)^###[ \t]+fixed\b'
    Assert-Contract ([bool]($plannedHeadingMatch -and $plannedAddedMatch -and $plannedNoFixedMatch)) "A coherent planned first-release changelog was rejected by the planned first-release state check."

    $contradictoryPlannedRelease = @"
# Changelog

## [Unreleased]

### Fixed

- Corrected a release behavior.

## [0.1.0] - 2026-09-15

### Added

- The first public tool contract is documented.
"@
    $contradictoryPlannedHeadingMatch = $contradictoryPlannedRelease -notmatch '(?im)^##[ \t]+\[0\.1\.0\][ \t]+-[ \t]+planned[ \t]+\(not[ \t-]+yet[ \t-]+published\)[ \t]*(?:\r)?$'
    $contradictoryFixedMatch = $contradictoryPlannedRelease -match '(?im)^##[ \t]+\[unreleased\][\s\S]*^###[ \t]+fixed\b'
    Assert-Contract ([bool]($contradictoryPlannedHeadingMatch -and $contradictoryFixedMatch)) "A contradictory released-plus-unreleased-fixes changelog passed the planned first-release state check."

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
        $trackedMismatch = Invoke-PwshScript -ScriptPath $changelogScriptPath -Arguments @(
            "-ExpectedVersion", "0.1.0",
            "-ExpectedPackageVersion", "0.1.0",
            "-ExpectedCommit", $script:RepositoryCommit,
            "-RepositoryRoot", $contractRepositoryRoot
        )
        Assert-Contract ($trackedMismatch.ExitCode -ne 0) "A tracked changelog modified after the checked-out commit passed the publication gate."
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
