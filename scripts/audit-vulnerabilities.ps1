[CmdletBinding()]
param(
    [string]$SolutionPath = "KeelMatrix.FixtureVault.sln",
    [string]$DotnetCommand = "dotnet"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Fail-Audit {
    param([string]$Message)

    Write-Error "Dependency vulnerability audit failed closed: $Message"
    exit 1
}

$arguments = @(
    "list",
    $SolutionPath,
    "package",
    "--vulnerable",
    "--include-transitive"
)

try {
    $auditLines = @(& $DotnetCommand @arguments 2>&1)
    $auditExitCode = $LASTEXITCODE
}
catch {
    Fail-Audit "the advisory command could not be started."
}

$auditOutput = ($auditLines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
if (-not [string]::IsNullOrWhiteSpace($auditOutput)) {
    Write-Output $auditOutput
}

if ($auditExitCode -ne 0) {
    Fail-Audit "the advisory command exited with code $auditExitCode."
}

if ([string]::IsNullOrWhiteSpace($auditOutput)) {
    Fail-Audit "the advisory command returned no data."
}

$unavailablePattern = '(?im)(?:audit|advisory|vulnerab\w*)[^\r\n]*(?:unavailable|not available|no data|could not|unable to|failed to)'
if ([regex]::IsMatch($auditOutput, $unavailablePattern)) {
    Fail-Audit "the advisory data was unavailable."
}

$cleanPattern = '(?im)^\s*The (?:given )?project .+ has no vulnerable packages given the (?:current )?sources(?: listed)?\.\s*$'
$vulnerablePattern = '(?im)^\s*The (?:given )?project .+ has the following vulnerable packages\s*:?\s*$'
$cleanResults = [regex]::Matches($auditOutput, $cleanPattern).Count
$vulnerableResults = [regex]::Matches($auditOutput, $vulnerablePattern).Count

if ($vulnerableResults -gt 0) {
    Fail-Audit "one or more applicable vulnerable packages were reported."
}

if (($cleanResults + $vulnerableResults) -eq 0) {
    Fail-Audit "the advisory command returned no recognized vulnerability result; audit availability could not be proven."
}

Write-Host "Dependency vulnerability audit passed: $cleanResults project result(s) reported no vulnerable packages; transitive dependencies were included."
