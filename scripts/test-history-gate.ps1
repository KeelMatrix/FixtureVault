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

function Add-TestCase {
    param(
        [Parameter(Mandatory = $true)]
        [object]$List,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$Body
    )

    $null = $List.Add([pscustomobject]@{
            Name = $Name
            Body = $Body
        })
}

function Format-Cell {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $Value.Replace("`r", "<CR>").Replace("`n", "<LF>").Replace("`t", "<TAB>").Replace(" ", "<SPACE>")
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

$hookPath = Join-Path $repositoryRoot ".githooks/commit-msg"
$hookText = [IO.File]::ReadAllText($hookPath)
$prefixMatch = [regex]::Match($hookText, "(?m)^internal_prefixes='([^']*)'")
Assert-Contract $prefixMatch.Success "The commit-message hook must expose its configured internal-prefix list."
$internalPrefixes = @($prefixMatch.Groups[1].Value -split "\s+" | Where-Object { $_ })
Assert-Contract ($internalPrefixes.Count -gt 0) "The commit-message hook must configure at least one internal prefix."

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$messageCounter = 0

function Invoke-Hook {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    $script:messageCounter++
    $messageName = "message-$script:messageCounter.txt"
    $messagePath = Join-Path $script:temporaryRoot $messageName
    [IO.File]::WriteAllText($messagePath, $Message, $script:utf8NoBom)

    $shellArguments = @("-c", "export PATH=/usr/bin:/bin:`$PATH; ./.githooks/commit-msg $messageName")
    if ([IO.Path]::GetFileName($script:shellPath) -eq "bash.exe") {
        $shellArguments = @("--noprofile", "--norc", "-c", "export PATH=/usr/bin:/bin:`$PATH; ./.githooks/commit-msg $messageName")
    }

    $output = @(& $script:shellPath @shellArguments 2>&1)
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = $output
    }
}

function Assert-HookCase {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Case,
        [Parameter(Mandatory = $true)]
        [int]$ExpectedExitCode
    )

    $result = Invoke-Hook -Message $Case.Body
    $detail = ($result.Output -join [Environment]::NewLine)
    Assert-Contract ($result.ExitCode -eq $ExpectedExitCode) "Case '$($Case.Name)' returned exit $($result.ExitCode), expected $ExpectedExitCode. Output: $detail"
}

$rejectCases = [System.Collections.Generic.List[object]]::new()
$acceptCases = [System.Collections.Generic.List[object]]::new()

# The technical corpus is retained as compatibility coverage for the existing
# history contract; these are technical tokens, not internal task identifiers.
$technicalRejects = @(
    "Fix UTF-8-1",
    "Fix UTF-16-1",
    "Fix SHA-256-1",
    "Fix HTTP-2-1",
    "Fix TLS-1.2-1",
    "Fix NET-8-1",
    "Fix FV007-1",
    "Fix UTF-8-123",
    "Fix SHA-256-42",
    "Fix UTF-8_1",
    "Fix UTF-8#1",
    "Fix UTF-8/1",
    "Fix UTF-8.1",
    "Fix UTF-8:1",
    "Fix UTF-8~1",
    "Fix UTF-8+1",
    "Fix UTF-8,1",
    "Fix UTF-8;1",
    "Fix UTF-8=1",
    "Fix UTF-8|1",
    "Fix UTF-8@1",
    "Fix UTF-8^1",
    "Fix UTF-8&1",
    "Fix UTF-8*1",
    "Fix UTF-8(1)",
    "Fix UTF-8[1]",
    "Fix UTF-8{1}",
    "Fix UTF-8<1>",
    'Fix UTF-8"1',
    "Fix UTF-8'1",
    "Fix SHA-256_1",
    "Fix SHA-256#1",
    "Fix SHA-256/1",
    "Fix SHA-256.1",
    "Fix SHA-256:1",
    "Fix TLS-1.2#1",
    "Fix TLS-1.2/1",
    "Fix TLS-1.2.1",
    "Fix NET8.0_1",
    "Fix NET8.0.1",
    "Fix NET8.0#1",
    "Fix NET8.0/1",
    "Fix NET-8_1",
    "Fix FV007_1",
    "Fix FV-E016#1",
    "Fix RFC-9110_1",
    "Fix AES-256_1",
    "Fix HMAC-256#1",
    "Fix CVE-2021-44228_1",
    "UTF-8_1_2",
    "UTF-8_123",
    "ISO-8601-1",
    "IEEE-754-2019",
    "SHA-256/384",
    "net8.0.1"
)
for ($index = 0; $index -lt $technicalRejects.Count; $index++) {
    Add-TestCase -List $rejectCases -Name "technical reject $index" -Body $technicalRejects[$index]
}

$technicalAccepts = @(
    "UTF-8",
    "UTF-16",
    "UTF-32",
    "UTF-16LE",
    "UTF-16BE",
    "UTF-32LE",
    "UTF-32BE",
    "UTF-16/UTF-32",
    "UTF-16LE/BE",
    "UTF-32LE/BE",
    "LATIN-1",
    "SHA-1",
    "SHA-256",
    "SHA-384",
    "SHA-512",
    "MD5-5",
    "HTTP-2",
    "HTTP-3",
    "TLS-1",
    "TLS-1.2",
    "TLS-1.3",
    "SSL-3",
    "RFC-9110",
    "RFC-2119",
    "ISO-8601",
    "IEEE-754",
    "ECMA-335",
    "AES-256",
    "AES-256-GCM",
    "HMAC-256",
    "RSA-2048",
    "MIME-1",
    "CVE-2021-44228",
    "net8.0",
    "net10.0",
    "NET-8",
    "FV007",
    "FV-E016",
    "FV-SKIP-ENCODING"
)
for ($index = 0; $index -lt $technicalAccepts.Count; $index++) {
    Add-TestCase -List $acceptCases -Name "technical accept $index" -Body $technicalAccepts[$index]
}

$engineeringAccepts = @(
    "Fix UTF-8 decoding",
    "Add AES-256-GCM support",
    "Support HTTP-2 and HTTP-3",
    "Upgrade TLS-1.3 support",
    "Parse RFC-9110 headers",
    "Hash with SHA-256",
    "UTF-8 1",
    "UTF-8`n1",
    "UTF-8`r`n1",
    "U+0000",
    "Preserve UTF-8, UTF-16, and UTF-32 decoding",
    "UTF-8١",
    "AVX 512",
    "ARM 64",
    "CUDA 12",
    "Go 1.22",
    "ABC 1",
    "UTF-8 1"
)
for ($index = 0; $index -lt $engineeringAccepts.Count; $index++) {
    Add-TestCase -List $acceptCases -Name "engineering accept $index" -Body $engineeringAccepts[$index]
}

# Rule A: generic prefixes are rejected when a separator joins their numeric
# continuation, regardless of separator repetition or line-break layout.
$genericSeparators = @("-", "--", "_", ".", "#", "/")
foreach ($separator in $genericSeparators) {
    Add-TestCase -List $rejectCases -Name "generic separator $separator" -Body ("ABC`n$separator" + "3")
}
Add-TestCase -List $rejectCases -Name "generic direct separator" -Body "ABC-1"
Add-TestCase -List $rejectCases -Name "generic em dash separator" -Body ("ABC`nem dash".Replace("em dash", [string][char]0x2014) + "1")
Add-TestCase -List $rejectCases -Name "generic NBSP then separator" -Body ("ABC`n" + [string][char]0x00a0 + "_1")

# Rule B cases are derived from the hook's configured list; no configured
# internal prefix is duplicated in this contract script.
foreach ($prefix in $internalPrefixes) {
    Add-TestCase -List $rejectCases -Name "configured prefix hyphen" -Body "$prefix-3"
    Add-TestCase -List $rejectCases -Name "configured prefix space" -Body "$prefix 3"
    Add-TestCase -List $rejectCases -Name "configured prefix line break" -Body "$prefix`n3"
    foreach ($separator in @("_", "--", ".", "#", "/")) {
        Add-TestCase -List $rejectCases -Name "configured prefix separator $separator" -Body ("$prefix`n$separator" + "3")
    }
    Add-TestCase -List $rejectCases -Name "configured prefix NBSP" -Body ("$prefix`n" + [string][char]0x00a0 + "3")
}

# Rule C cases use synthetic prefixes so the test proves prefix-agnostic
# matching without embedding repository metadata.
$referenceCases = @(
    "refs QZW-589",
    "refs`nQZW-589",
    "closes ZZZ-34",
    "fixes QZW-1",
    "resolves`r`nZZZ-34",
    "reopens QZW-1",
    "issue: QZW-589",
    "task #ZZZ-34",
    "related`n to QZW-1",
    "part of QZW-589"
)
for ($index = 0; $index -lt $referenceCases.Count; $index++) {
    Add-TestCase -List $rejectCases -Name "keyword reference $index" -Body $referenceCases[$index]
}

# This plain literal is the rule's own review-process vocabulary, not internal
# metadata. Keep it only where Rule D itself is exercised.
$reviewWord = "frontier"
$reviewCases = @(
    $reviewWord,
    "$reviewWord review",
    "$reviewWord`nreview",
    "rejection round",
    "rejection`nround",
    "review round",
    "review`r`nround",
    "acceptance pass",
    "acceptance`npass"
)
for ($index = 0; $index -lt $reviewCases.Count; $index++) {
    Add-TestCase -List $rejectCases -Name "review wording $index" -Body $reviewCases[$index]
}

# Identity trailers remain rejected after normalization.
Add-TestCase -List $rejectCases -Name "identity trailer" -Body "co-authored-by: Example <example@example.com>"
Add-TestCase -List $rejectCases -Name "identity trailer after line break" -Body "Summary`nsigned-off-by: Example <example@example.com>"

$previousLocation = Get-Location
try {
    Push-Location $temporaryRoot

    # The separator matrix is independent of hand-picked rows: every token
    # contains an ASCII letter and every cell adds a separator plus ASCII
    # digits. Every cell is therefore required to reject under Rule A.
    $matrixTokens = @(
        "UTF-8",
        "UTF-16LE",
        "SHA-256",
        "TLS-1.2",
        "RFC-9110",
        "AES-256-GCM",
        "CVE-2021-44228",
        "NET8.0",
        "ALPHA",
        "Build",
        "Token",
        "ZZZ"
    )
    $matrixSeparators = @("-", "_", ".", "#", "/", ":", "~", "+", ",", ";", "=", "|", "\", "@", "^", "&", "*", "(", ")", "[", "]", "{", "}", "<", ">", '"', "'", [string][char]0x60)
    $matrixLayouts = @("")
    $matrixSuffixes = @("1")

    $matrixLines = [System.Collections.Generic.List[string]]::new()
    $matrixFileCounter = 0
    foreach ($case in $rejectCases) {
        $matrixFileCounter++
        $messageName = "matrix-$matrixFileCounter.bin"
        [IO.File]::WriteAllBytes((Join-Path $temporaryRoot $messageName), $utf8NoBom.GetBytes($case.Body))
        $matrixLines.Add("1 $messageName")
    }
    foreach ($case in $acceptCases) {
        $matrixFileCounter++
        $messageName = "matrix-$matrixFileCounter.bin"
        [IO.File]::WriteAllBytes((Join-Path $temporaryRoot $messageName), $utf8NoBom.GetBytes($case.Body))
        $matrixLines.Add("0 $messageName")
    }
    $decisionMatrixCells = $matrixLines.Count
    $separatorMatrixCells = 0
    foreach ($token in $matrixTokens) {
        foreach ($layout in $matrixLayouts) {
            foreach ($separator in $matrixSeparators) {
                foreach ($suffix in $matrixSuffixes) {
                    $separatorMatrixCells++
                    $message = $token + $layout + $separator + $layout + $suffix
                    $matrixFileCounter++
                    $messageName = "matrix-$matrixFileCounter.bin"
                    [IO.File]::WriteAllBytes((Join-Path $temporaryRoot $messageName), $utf8NoBom.GetBytes($message))
                    $matrixLines.Add("1 $messageName")
                }
            }
        }
    }

    # Pure ASCII-whitespace continuations are the documented deliberate
    # accepts for generic tokens and allowlisted technical tokens.
    $whitespaceTokens = @("AVX", "ARM", "CUDA", "Go", "ABC", "UTF-8")
    $whitespaceLayouts = @(" ", "`n", "`r`n", "`t", " `n", "`n ")
    $whitespaceSuffixes = @("1", "1.2")
    $whitespaceMatrixAccepted = [System.Collections.Generic.List[string]]::new()
    $whitespaceMatrixCells = 0
    foreach ($token in $whitespaceTokens) {
        foreach ($layout in $whitespaceLayouts) {
            foreach ($suffix in $whitespaceSuffixes) {
                $whitespaceMatrixCells++
                $message = $token + $layout + $suffix
                $matrixFileCounter++
                $messageName = "matrix-$matrixFileCounter.bin"
                [IO.File]::WriteAllBytes((Join-Path $temporaryRoot $messageName), $utf8NoBom.GetBytes($message))
                $matrixLines.Add("0 $messageName")
                $whitespaceMatrixAccepted.Add((Format-Cell -Value $message))
            }
        }
    }

    # Run every generated cell inside one POSIX process. This preserves
    # per-cell execution of the real hook while avoiding a Windows process
    # startup for every matrix row.
    $matrixInputPath = Join-Path $temporaryRoot "matrix-input.txt"
    [IO.File]::WriteAllText($matrixInputPath, ($matrixLines -join [Environment]::NewLine), $utf8NoBom)
    $matrixRunnerPath = Join-Path $temporaryRoot "run-matrix.sh"
    $matrixRunner = @'
#!/bin/sh
set -eu

input_file=$1

while IFS=' ' read -r expected message_file; do
  expected=$(printf '%s' "$expected" | tr -d '\r')
  message_file=$(printf '%s' "$message_file" | tr -d '\r')
  if [ -z "$message_file" ]; then
    continue
  fi
  if ./.githooks/commit-msg "$message_file" >/dev/null 2>&1; then
    actual=0
  else
    actual=$?
  fi
  if [ "$actual" -ne "$expected" ]; then
    printf 'Generated history-gate matrix mismatch: expected %s, got %s.\n' "$expected" "$actual" >&2
    exit 1
  fi
done < "$input_file"
'@
    [IO.File]::WriteAllText($matrixRunnerPath, $matrixRunner, [Text.Encoding]::ASCII)
    $matrixArguments = @("-c", "export PATH=/usr/bin:/bin:`$PATH; sh ./run-matrix.sh matrix-input.txt")
    if ([IO.Path]::GetFileName($shellPath) -eq "bash.exe") {
        $matrixArguments = @("--noprofile", "--norc", "-c", "export PATH=/usr/bin:/bin:`$PATH; sh ./run-matrix.sh matrix-input.txt")
    }
    $matrixOutput = @(& $shellPath @matrixArguments 2>&1)
    $matrixExitCode = $LASTEXITCODE
    Assert-Contract ($matrixExitCode -eq 0) "Generated history-gate matrix failed. Output: $($matrixOutput -join [Environment]::NewLine)"

    # Exercise the temporary history repository so the identity check still
    # proves that author and committer policy is enforced.
    $init = Invoke-Git @("init", "--quiet")
    Assert-Contract ($init.ExitCode -eq 0) "Could not initialize the temporary repository: $($init.Output -join [Environment]::NewLine)"
    $configName = Invoke-Git @("config", "user.name", "KeelMatrix")
    Assert-Contract ($configName.ExitCode -eq 0) "Could not configure the temporary repository author name."
    $configEmail = Invoke-Git @("config", "user.email", "keelmatrix@gmail.com")
    Assert-Contract ($configEmail.ExitCode -eq 0) "Could not configure the temporary repository author email."

    $validCommit = Invoke-Git @("commit", "--allow-empty", "-m", "Create test history")
    Assert-Contract ($validCommit.ExitCode -eq 0) "Could not create the valid test commit: $($validCommit.Output -join [Environment]::NewLine)"

    $previousAuthorName = $env:GIT_AUTHOR_NAME
    $previousAuthorEmail = $env:GIT_AUTHOR_EMAIL
    $previousCommitterName = $env:GIT_COMMITTER_NAME
    $previousCommitterEmail = $env:GIT_COMMITTER_EMAIL
    try {
        $env:GIT_AUTHOR_NAME = "Example Author"
        $env:GIT_AUTHOR_EMAIL = "example.author@example.com"
        $env:GIT_COMMITTER_NAME = "Example Committer"
        $env:GIT_COMMITTER_EMAIL = "example.committer@example.com"
        $invalidCommit = Invoke-Git @("commit", "--allow-empty", "-m", "Create invalid identity")
        Assert-Contract ($invalidCommit.ExitCode -eq 0) "Could not create the invalid identity test commit: $($invalidCommit.Output -join [Environment]::NewLine)"
    }
    finally {
        if ($null -eq $previousAuthorName) { Remove-Item Env:GIT_AUTHOR_NAME -ErrorAction SilentlyContinue } else { $env:GIT_AUTHOR_NAME = $previousAuthorName }
        if ($null -eq $previousAuthorEmail) { Remove-Item Env:GIT_AUTHOR_EMAIL -ErrorAction SilentlyContinue } else { $env:GIT_AUTHOR_EMAIL = $previousAuthorEmail }
        if ($null -eq $previousCommitterName) { Remove-Item Env:GIT_COMMITTER_NAME -ErrorAction SilentlyContinue } else { $env:GIT_COMMITTER_NAME = $previousCommitterName }
        if ($null -eq $previousCommitterEmail) { Remove-Item Env:GIT_COMMITTER_EMAIL -ErrorAction SilentlyContinue } else { $env:GIT_COMMITTER_EMAIL = $previousCommitterEmail }
    }

    $shellArguments = @("-c", "export PATH=/usr/bin:/bin:`$PATH; ./.githooks/check-history")
    if ([IO.Path]::GetFileName($shellPath) -eq "bash.exe") {
        $shellArguments = @("--noprofile", "--norc", "-c", "export PATH=/usr/bin:/bin:`$PATH; ./.githooks/check-history")
    }
    $gateOutput = @(& $shellPath @shellArguments 2>&1)
    $gateExitCode = $LASTEXITCODE
    $gateText = $gateOutput -join [Environment]::NewLine
    Assert-Contract ($gateExitCode -ne 0) "The history gate accepted a commit with a non-standard author and committer identity. Output: $gateText"
    Assert-Contract ($gateText.Contains("author and committer must be KeelMatrix <keelmatrix@gmail.com>.", [StringComparison]::Ordinal)) "The history gate did not report the required identity rule. Output: $gateText"

    Write-Host "History gate contract passed: rejects=$($rejectCases.Count), accepts=$($acceptCases.Count), decisionMatrix=$decisionMatrixCells, separatorMatrix=$separatorMatrixCells, whitespaceMatrix=$whitespaceMatrixCells, totalMatrix=$($decisionMatrixCells + $separatorMatrixCells + $whitespaceMatrixCells), matrixAccepted=$($whitespaceMatrixAccepted.Count)."
    Write-Host "Generated matrix accepted-cell list (all are documented pure-whitespace continuations):"
    foreach ($cell in $whitespaceMatrixAccepted) {
        Write-Host "  $cell"
    }
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
