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

function Invoke-CommitMessageHook {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    [IO.File]::WriteAllText(
        (Join-Path $temporaryRoot "message.txt"),
        $Message + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    $shellArguments = @("-c", "export PATH=/usr/bin:/bin:`$PATH; ./.githooks/commit-msg message.txt")
    if ([IO.Path]::GetFileName($shellPath) -eq "bash.exe") {
        $shellArguments = @("--noprofile", "--norc", "-c", "export PATH=/usr/bin:/bin:`$PATH; ./.githooks/commit-msg message.txt")
    }
    $output = @(& $shellPath @shellArguments 2>&1)
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = $output
    }
}

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

    $positiveCorpus = @(
        "Accept SHA-1 digests from legacy manifests"
        "Use SHA-224 for compatibility vectors"
        "Fix SHA-256 hashing"
        "Switch cache keys to SHA-384"
        "Retain SHA-512 integrity checks"
        "Parse SHA-3 digest labels"
        "Remove MD5 from the default integrity policy"
        "Compare HMAC-256 signatures in fixture metadata"
        "Preserve UTF-8 BOM handling in reports"
        "Decode UTF-16 fixture files with a byte-order mark"
        "Keep UTF-32 metadata round trips deterministic"
        "Verify UTF-16LE/BE and UTF-32LE/BE baseline decoding"
        "Preserve UTF-16/UTF-32 BOM detection"
        "Reject invalid Unicode surrogate pairs"
        "Normalize NFC filenames before comparison"
        "Handle Latin-1 fixture input explicitly"
        "Support HTTP-2 request fixtures"
        "Add HTTP-3 protocol coverage"
        "Require TLS-1 for legacy endpoint tests"
        "Upgrade TLS-1.1 negotiation checks"
        "Retain TLS-1.2 compatibility coverage"
        "Upgrade TLS-1.3 support"
        "Reject SSL-3 fallback"
        "Parse RFC-9110 headers"
        "Apply RFC-2119 requirement wording"
        "Normalize ISO-8601 timestamps"
        "Preserve IEEE-754 float round trips"
        "Read ECMA-335 metadata tokens"
        "Cover AES-128 encrypted fixtures"
        "Cover AES-256 encrypted fixtures"
        "Validate RSA-2048 key metadata"
        "Parse MIME-1 multipart boundaries"
        "Record CVE-2026-1234 advisory metadata"
        "Keep Git worktree paths repository relative"
        "Preserve merge-base detection for shallow clones"
        "Ignore untracked fixture outputs"
        "Handle detached HEAD during package smoke"
        "Run Linux and Windows fixture checks"
        "Cache NuGet restore packages in CI"
        "Fail CI on malformed policy input"
        "Publish test results after scan failures"
        "Retry transient restore metadata reads"
        "Normalize Windows path separators"
        "Guard Unix symlink traversal"
        "Reject relative path segments"
        "Preserve Unicode filenames on macOS"
        "Detect case collisions on NTFS"
        "Keep CRLF content stable across platforms"
        "Add KeelMatrix.FixtureVault package metadata"
        "Include README and license in the nupkg"
        "Verify SourceLink commit metadata"
        "Pack the net8.0 tool command"
        "Validate nuspec repository URL"
        "Keep snupkg symbols beside the package"
        "Parse SemVer 2.0 prerelease labels"
        "Reject invalid version ranges"
        "Align package and tool versions"
        "Compare major minor patch components"
        "Document version 0.1.0 defaults"
        "Bound fixture enumeration memory"
        "Avoid repeated UTF-8 allocations"
        "Hash large files in a single pass"
        "Measure scan throughput on cold disk"
        "Skip duplicate directory stats"
        "Return exit code 2 for configuration errors"
        "Keep malformed JSON diagnostics concise"
        "Fail closed when content is uninspectable"
        "Do not echo secret values in errors"
        "Preserve actionable remediation text"
        "Add FV007 sensitive-data diagnostics"
        "Document FV-E016 uninspectable content errors"
        "Keep net8.0 tool startup deterministic"
        "Guard case-insensitive extension matching"
        "Report unsupported fixture conventions"
        "Read policy files without mutation"
        "Keep JSON report schema versioned"
        "Separate console and JSON renderers"
        "Use bounded file-size checks"
        "Handle empty fixture roots gracefully"
        "Verify no fixture bytes leave the process"
        "Retain stable rule ordering"
        "Make scan output reproducible"
    )

    $negativeCorpus = @(
        "KEE-3",
        "ABC-1",
        "KEE-589",
        "Refs ABC-12",
        "Refs KEE-589",
        "Closes ABC-12",
        "Fixes XYZ-34",
        "Part of KEE-3",
        "[KEE-3]",
        "(ABC-12)",
        "issue: KEE-3",
        "task #ABC-1",
        "related to KEE-589",
        "reopens KEE-3",
        "ABC-12345678",
        "frontier review",
        "frontier",
        "rejection round",
        "review round",
        "acceptance pass"
    )

    foreach ($message in $positiveCorpus) {
        $hookResult = Invoke-CommitMessageHook -Message $message
        Write-Host ("positive`t{0}`t{1}" -f $hookResult.ExitCode, $message)
        Assert-Contract ($hookResult.ExitCode -eq 0) "The commit-msg hook rejected legitimate engineering prose '$message'. Output: $($hookResult.Output -join [Environment]::NewLine)"
    }

    foreach ($message in $negativeCorpus) {
        $hookResult = Invoke-CommitMessageHook -Message $message
        Write-Host ("negative`t{0}`t{1}" -f $hookResult.ExitCode, $message)
        Assert-Contract ($hookResult.ExitCode -eq 1) "The commit-msg hook accepted prohibited metadata '$message'. Output: $($hookResult.Output -join [Environment]::NewLine)"
    }

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

Write-Host "History gate contract passed: prohibited task/review metadata and a non-standard author or committer identity were rejected; engineering identifiers were accepted."
