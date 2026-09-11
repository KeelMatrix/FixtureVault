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

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $output = @(& git @Arguments 2>&1)
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = $output
    }
}

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("fixturevault-history-gate-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $temporaryRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot ".githooks") -Destination (Join-Path $temporaryRoot ".githooks") -Recurse

$shellCommand = Get-Command sh -ErrorAction SilentlyContinue
if ($null -ne $shellCommand) {
    $shellPath = $shellCommand.Source
}
else {
    $gitCommand = Get-Command git -ErrorAction Stop
    $gitRoot = Split-Path -Parent (Split-Path -Parent $gitCommand.Source)
    $shellPath = Join-Path $gitRoot "usr/bin/bash.exe"
    Assert-Contract (Test-Path -LiteralPath $shellPath) "Could not find a POSIX shell for the history gate."
}

$previousAuthorName = $env:GIT_AUTHOR_NAME
$previousAuthorEmail = $env:GIT_AUTHOR_EMAIL
$previousCommitterName = $env:GIT_COMMITTER_NAME
$previousCommitterEmail = $env:GIT_COMMITTER_EMAIL

try {
    Push-Location $temporaryRoot

    $init = Invoke-Git @("init", "--quiet")
    Assert-Contract ($init.ExitCode -eq 0) "Could not initialize the temporary repository: $($init.Output -join [Environment]::NewLine)"

    $configName = Invoke-Git @("config", "user.name", "KeelMatrix")
    Assert-Contract ($configName.ExitCode -eq 0) "Could not configure the temporary repository author name."
    $configEmail = Invoke-Git @("config", "user.email", "keelmatrix@gmail.com")
    Assert-Contract ($configEmail.ExitCode -eq 0) "Could not configure the temporary repository author email."

    $validCommit = Invoke-Git @("commit", "--allow-empty", "-m", "Create test history")
    Assert-Contract ($validCommit.ExitCode -eq 0) "Could not create the valid test commit: $($validCommit.Output -join [Environment]::NewLine)"

    $env:GIT_AUTHOR_NAME = "Example Author"
    $env:GIT_AUTHOR_EMAIL = "example.author@example.com"
    $env:GIT_COMMITTER_NAME = "Example Committer"
    $env:GIT_COMMITTER_EMAIL = "example.committer@example.com"
    $invalidCommit = Invoke-Git @("commit", "--allow-empty", "-m", "Create invalid identity")
    Assert-Contract ($invalidCommit.ExitCode -eq 0) "Could not create the invalid identity test commit: $($invalidCommit.Output -join [Environment]::NewLine)"

    $shellArguments = @("-c", "export PATH=/usr/bin:/bin:`$PATH; sh ./.githooks/check-history")
    if ([IO.Path]::GetFileName($shellPath) -eq "bash.exe") {
        $shellArguments = @("--noprofile", "--norc", "-c", "export PATH=/usr/bin:/bin:`$PATH; sh ./.githooks/check-history")
    }
    $gateOutput = @(& $shellPath @shellArguments 2>&1)
    $gateExitCode = $LASTEXITCODE
    $gateText = $gateOutput -join [Environment]::NewLine
    Assert-Contract ($gateExitCode -ne 0) "The history gate accepted a commit with a non-standard author and committer identity."
    Assert-Contract ($gateText.Contains("author and committer must be KeelMatrix <keelmatrix@gmail.com>.", [StringComparison]::Ordinal)) "The history gate did not report the required identity rule. Output: $gateText"
}
finally {
    Pop-Location

    if ($null -eq $previousAuthorName) { Remove-Item Env:GIT_AUTHOR_NAME -ErrorAction SilentlyContinue } else { $env:GIT_AUTHOR_NAME = $previousAuthorName }
    if ($null -eq $previousAuthorEmail) { Remove-Item Env:GIT_AUTHOR_EMAIL -ErrorAction SilentlyContinue } else { $env:GIT_AUTHOR_EMAIL = $previousAuthorEmail }
    if ($null -eq $previousCommitterName) { Remove-Item Env:GIT_COMMITTER_NAME -ErrorAction SilentlyContinue } else { $env:GIT_COMMITTER_NAME = $previousCommitterName }
    if ($null -eq $previousCommitterEmail) { Remove-Item Env:GIT_COMMITTER_EMAIL -ErrorAction SilentlyContinue } else { $env:GIT_COMMITTER_EMAIL = $previousCommitterEmail }

    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Host "History gate contract passed: a commit with a non-standard author or committer identity was rejected."
