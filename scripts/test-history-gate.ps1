[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$timer = [Diagnostics.Stopwatch]::StartNew()
$completed = $false
$pushedLocation = $false
$positiveCount = 0
$negativeCount = 0
$matrixCount = 0
$matrixAcceptedCount = 0
$timeoutCount = 0

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
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]]$List,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$Body,
        [Parameter(Mandatory = $true)]
        [int]$ExpectedExitCode
    )

    $null = $List.Add([pscustomobject]@{
            Name = $Name
            Body = $Body
            ExpectedExitCode = $ExpectedExitCode
        })
}

function Invoke-CaseBatch {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.List[object]]$Cases,
        [Parameter(Mandatory = $true)]
        [string]$BatchName
    )

    $inputName = "$BatchName-input.txt"
    $inputPath = Join-Path $temporaryRoot $inputName
    $inputLines = [System.Collections.Generic.List[string]]::new()
    $caseNumber = 0

    foreach ($case in $Cases) {
        $caseNumber++
        $messageName = "$BatchName-message-$caseNumber.txt"
        $messagePath = Join-Path $temporaryRoot $messageName
        [IO.File]::WriteAllText($messagePath, $case.Body, $utf8NoBom)
        $inputLines.Add("$($case.ExpectedExitCode)|$messageName|$caseNumber")
    }

    [IO.File]::WriteAllLines($inputPath, $inputLines, $utf8NoBom)

    $runnerName = "run-cases.sh"
    $runnerPath = Join-Path $temporaryRoot $runnerName
    if (-not (Test-Path -LiteralPath $runnerPath)) {
        $runner = @'
#!/bin/sh
set -eu

worker_limit=32
active_workers=0

run_case() {
  expected=$1
  message_file=$2
  case_number=$3
  if timeout 5s ./.githooks/commit-msg "$message_file" >/dev/null 2>&1; then
    actual=0
  else
    actual=$?
  fi
  printf '%s|%s|%s\n' "$case_number" "$expected" "$actual"
}

while IFS='|' read -r expected message_file case_number; do
  if [ -z "$message_file" ]; then
    continue
  fi
  run_case "$expected" "$message_file" "$case_number" &
  active_workers=$((active_workers + 1))
  if [ "$active_workers" -ge "$worker_limit" ]; then
    wait
    active_workers=0
  fi
done < "$1"
wait
'@
        [IO.File]::WriteAllText($runnerPath, $runner, [Text.Encoding]::ASCII)
    }

    $command = "export PATH=/usr/bin:/bin:`$PATH; sh $runnerName $inputName"
    $processInfo = [Diagnostics.ProcessStartInfo]::new()
    $processInfo.FileName = $shellPath
    $processInfo.WorkingDirectory = $temporaryRoot
    $processInfo.UseShellExecute = $false
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    if ([IO.Path]::GetFileName($shellPath) -eq "bash.exe") {
        $null = $processInfo.ArgumentList.Add("--noprofile")
        $null = $processInfo.ArgumentList.Add("--norc")
    }
    $null = $processInfo.ArgumentList.Add("-c")
    $null = $processInfo.ArgumentList.Add($command)

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $processInfo
    Assert-Contract $process.Start() "Could not start the $BatchName case runner."
    $finished = $process.WaitForExit(150000)
    if (-not $finished) {
        $process.Kill($true)
        $process.WaitForExit()
        throw "$BatchName case runner exceeded its 150-second batch timeout."
    }

    $output = $process.StandardOutput.ReadToEnd()
    $errorOutput = $process.StandardError.ReadToEnd()
    $exitCode = $process.ExitCode
    Assert-Contract ($exitCode -eq 0) "$BatchName case runner failed with exit $exitCode. Output: $output $errorOutput"

    $results = [System.Collections.Generic.List[object]]::new()
    $mismatches = [System.Collections.Generic.List[string]]::new()
    $outputLines = @($output -split "`r?`n" | Where-Object { $_ -ne "" })
    foreach ($line in $outputLines) {
        $fields = $line -split "\|"
        Assert-Contract ($fields.Count -eq 3) "$BatchName case runner returned malformed output: $line"
        $result = [pscustomobject]@{
            CaseNumber = [int]$fields[0]
            ExpectedExitCode = [int]$fields[1]
            ActualExitCode = [int]$fields[2]
        }
        $null = $results.Add($result)
        if ($result.ActualExitCode -eq 124 -or $result.ActualExitCode -eq 137) {
            $script:timeoutCount++
        }
        if ($result.ActualExitCode -ne $result.ExpectedExitCode) {
            $null = $mismatches.Add("case $($result.CaseNumber): expected $($result.ExpectedExitCode), got $($result.ActualExitCode)")
        }
    }

    Assert-Contract ($results.Count -eq $Cases.Count) "$BatchName returned $($results.Count) results for $($Cases.Count) cases."
    Assert-Contract ($mismatches.Count -eq 0) "$BatchName mismatches: $($mismatches -join '; ')"

    [pscustomobject]@{
        Count = $results.Count
        Results = @($results)
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

$timeoutArguments = @("-c", "command -v timeout")
if ([IO.Path]::GetFileName($shellPath) -eq "bash.exe") {
    $timeoutArguments = @("--noprofile", "--norc", "-c", "command -v timeout")
}
$timeoutProbe = & $shellPath @timeoutArguments 2>&1
Assert-Contract ($LASTEXITCODE -eq 0) "The history gate requires a POSIX timeout command for bounded per-case execution."

$hookText = [IO.File]::ReadAllText((Join-Path $repositoryRoot ".githooks/commit-msg"))
$prefixMatch = [regex]::Match($hookText, "(?m)^internal_prefixes='([^']*)'")
Assert-Contract $prefixMatch.Success "The commit-message hook must expose its configured internal-prefix list."
$internalPrefixes = @($prefixMatch.Groups[1].Value -split "\s+" | Where-Object { $_ })
Assert-Contract ($internalPrefixes.Count -gt 0) "The commit-message hook must configure at least one internal prefix."

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$positiveCases = [System.Collections.Generic.List[object]]::new()
$negativeCases = [System.Collections.Generic.List[object]]::new()
$matrixCases = [System.Collections.Generic.List[object]]::new()

# This is the complete 104-line positive corpus from the prior history-gate
# contract. It is kept as a named baseline so removals or rewrites fail loudly.
$previousPositiveCorpus = @(
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
    "Use UTF-8 and UTF-16 encodings"
    "Preserve UTF-16LE byte order"
    "Preserve UTF-32BE byte order"
    "Hash with SHA-1"
    "Hash with SHA-256"
    "Hash with SHA-512"
    "Validate MD5-5 compatibility"
    "Negotiate HTTP-2"
    "Negotiate TLS-1.2"
    "Parse RFC-9110 metadata"
    "Apply ISO-8601 timestamps"
    "Read IEEE-754 values"
    "Inspect ECMA-335 metadata"
    "Encrypt with AES-256"
    "Authenticate with HMAC-256"
    "Load RSA-2048 keys"
    "Parse MIME-1 content"
    "Track CVE-2021-44228 advisories"
    "Run the net8.0 tool"
    "Report FV007 findings"
    "Report FV-E016 errors"
    "Skip FV-SKIP-ENCODING diagnostics"
)
Assert-Contract ($previousPositiveCorpus.Count -eq 104) "The preserved positive corpus must contain exactly 104 lines."

$additionalPositiveCorpus = @(
    "Document SHA-256/384 compatibility aliases"
    "Track net8.0.1 patch behavior"
    "Preserve UTF-8 1 fallback parsing"
    "Handle PROJ-42 public fixture references"
    "Validate ISO-8601-1 date suffixes"
    "Record IEEE-754-2019 numeric fixtures"
    "Compare CVE-2026-1234 advisory fields"
    "Cover AES-128 and AES-256 fixture encryption"
    "Parse TLS-1.1 negotiation traces"
    "Normalize LF and CRLF fixture endings"
    "Bound retry loops during package restore"
    "Keep source-generated diagnostics stable"
    "Verify case-sensitive path policies"
    "Avoid following links outside fixture roots"
    "Keep JSON reports deterministic"
    "Measure cold-start scan latency"
    "Preserve empty-directory scan behavior"
    "Reject malformed policy documents safely"
    "Inspect packages without mutating sources"
    "Document unsupported snapshot conventions"
)

$positiveCorpus = @($previousPositiveCorpus + $additionalPositiveCorpus)
Assert-Contract ($positiveCorpus.Count -ge 120) "The positive corpus must contain at least 120 lines."
for ($index = 0; $index -lt $previousPositiveCorpus.Count; $index++) {
    Add-TestCase -List $positiveCases -Name "positive-preserved-$($index + 1)" -Body $previousPositiveCorpus[$index] -ExpectedExitCode 0
}
for ($index = 0; $index -lt $additionalPositiveCorpus.Count; $index++) {
    Add-TestCase -List $positiveCases -Name "positive-added-$($index + 1)" -Body $additionalPositiveCorpus[$index] -ExpectedExitCode 0
}

$asciiPunctuation = @(
    '!', '"', '#', '$', '%', '&', "'", '(', ')', '*', '+', ',', '-', '.', '/',
    ':', ';', '<', '=', '>', '?', '@', '[', '\', ']', '^', '_', '`', '{', '|', '}', '~'
)
$repeatedSeparators = @('--', '||')
$nonAsciiSeparators = @(' ', '—')
$matrixSeparators = @($asciiPunctuation + $repeatedSeparators + $nonAsciiSeparators)
$negativeLayouts = @(
    [pscustomobject]@{ Name = "direct"; Before = ""; After = "" }
    [pscustomobject]@{ Name = "LF"; Before = "`n"; After = "`n" }
    [pscustomobject]@{ Name = "CR"; Before = "`r"; After = "`r" }
    [pscustomobject]@{ Name = "CRLF"; Before = "`r`n"; After = "`r`n" }
    [pscustomobject]@{ Name = "TAB"; Before = "`t"; After = "`t" }
    [pscustomobject]@{ Name = "NBSP"; Before = " "; After = " " }
    [pscustomobject]@{ Name = "form-feed"; Before = "`f"; After = "`f" }
    [pscustomobject]@{ Name = "vertical-tab"; Before = "`v"; After = "`v" }
    [pscustomobject]@{ Name = "whitespace-run"; Before = "  "; After = "  " }
)
$matrixLayouts = @($negativeLayouts[0], $negativeLayouts[1], $negativeLayouts[3])
$matrixSuffixes = @("1", "12345678")

foreach ($prefix in $internalPrefixes) {
    Add-TestCase -List $negativeCases -Name "prefix-alone-$prefix" -Body $prefix -ExpectedExitCode 1
    foreach ($layout in $negativeLayouts) {
        Add-TestCase -List $negativeCases -Name "prefix-whitespace-$prefix-$($layout.Name)" -Body ($prefix + $layout.Before + "3" + $layout.After) -ExpectedExitCode 1
    }
    foreach ($separator in $matrixSeparators) {
        foreach ($suffix in $matrixSuffixes) {
            foreach ($layout in $matrixLayouts) {
                $message = $prefix + $layout.Before + $separator + $layout.After + $suffix
                Add-TestCase -List $matrixCases -Name "matrix-$prefix-$($layout.Name)-$suffix" -Body $message -ExpectedExitCode 1
            }
        }
    }
    Add-TestCase -List $negativeCases -Name "keyword-led-reference-$prefix" -Body "refs $prefix-3" -ExpectedExitCode 1
    Add-TestCase -List $negativeCases -Name "keyword-led-reference-line-$prefix" -Body ("refs`n" + $prefix + "`n-3") -ExpectedExitCode 1
}

$internalVocabulary = @("paperclip", "codex", "chatgpt", "openai", "claude", "copilot", "gemini", "anthropic", "orchestration", "orchestrator")
foreach ($word in $internalVocabulary) {
    Add-TestCase -List $negativeCases -Name "vocabulary-$word" -Body $word -ExpectedExitCode 1
    Add-TestCase -List $negativeCases -Name "vocabulary-case-$word" -Body ("Fix " + $word.ToUpperInvariant() + " metadata") -ExpectedExitCode 1
    foreach ($separator in @(" ", "-", "_", ".", "/", " ", "—")) {
        Add-TestCase -List $negativeCases -Name "vocabulary-boundary-$word" -Body ("Fix" + $separator + $word + $separator + "metadata") -ExpectedExitCode 1
    }
}

$trailerLabels = @("co-authored-by", "signed-off-by", "reviewed-by", "tested-by", "acked-by", "reported-by", "approved-by", "agent", "model", "prompt")
$trailerLayouts = @("`n", "`r", "`r`n")
foreach ($label in $trailerLabels) {
    foreach ($lineBreak in $trailerLayouts) {
        Add-TestCase -List $negativeCases -Name "trailer-$label-$($lineBreak.Length)" -Body ("Update parser" + $lineBreak + $label + ": Example <example@example.com>") -ExpectedExitCode 1
    }
}

$phraseLayouts = @(" ", "  ", "`n", "`r", "`r`n", "`t", " ", "`f", "`v")
$reviewPhrases = @("frontier", "review round", "review pass", "acceptance round", "acceptance pass")
foreach ($phrase in $reviewPhrases) {
    if ($phrase -eq "frontier") {
        Add-TestCase -List $negativeCases -Name "review-$phrase" -Body $phrase -ExpectedExitCode 1
    }
    else {
        foreach ($layout in $phraseLayouts) {
            Add-TestCase -List $negativeCases -Name "review-$($phrase.Replace(' ', '-'))-$($layout.Length)" -Body $phrase.Replace(" ", $layout) -ExpectedExitCode 1
        }
    }
}

$generatedLayouts = @(" ", "  ", "`n", "`r", "`r`n", "`t", " ", "`f", "`v")
foreach ($layout in $generatedLayouts) {
    Add-TestCase -List $negativeCases -Name "generated-by-$($layout.Length)" -Body ("generated" + $layout + "by") -ExpectedExitCode 1
}

try {
    Push-Location $temporaryRoot
    $pushedLocation = $true

    Assert-Contract ($asciiPunctuation.Count -eq 32) "The matrix must cover all 32 ASCII punctuation separators."
    Assert-Contract ($matrixCases.Count -gt 150) "The generated internal-prefix matrix is too small."

    $positiveResult = Invoke-CaseBatch -Cases $positiveCases -BatchName "positive"
    $positiveCount = $positiveResult.Count
    Assert-Contract ($positiveCount -eq $positiveCorpus.Count) "Positive corpus result count did not match the corpus."

    $negativeResult = Invoke-CaseBatch -Cases $negativeCases -BatchName "negative"
    $negativeCount = $negativeResult.Count

    $matrixResult = Invoke-CaseBatch -Cases $matrixCases -BatchName "matrix"
    $matrixCount = $matrixResult.Count
    $acceptedCells = @($matrixResult.Results | Where-Object { $_.ActualExitCode -eq 0 })
    $matrixAcceptedCount = $acceptedCells.Count
    Assert-Contract ($matrixAcceptedCount -eq 0) "The generated internal-prefix matrix accepted $matrixAcceptedCount cells."

    Write-Host "Positive corpus: preserved=104, added=$($additionalPositiveCorpus.Count), total=$positiveCount, all exit 0."
    Write-Host "Negative corpus: cases=$negativeCount, all exit 1."
    Write-Host "Generated matrix: cells=$matrixCount, accepted-cell list:"
    Write-Host "  (empty)"
    Write-Host "Corpus integrity: preserved=104; moved=0; rewritten=0; added=$($additionalPositiveCorpus.Count)."

    $init = Invoke-Git @("init", "--quiet")
    Assert-Contract ($init.ExitCode -eq 0) "Could not initialize the temporary history repository: $($init.Output -join [Environment]::NewLine)"
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

    $historyArguments = @("-c", "export PATH=/usr/bin:/bin:`$PATH; sh ./.githooks/check-history")
    if ([IO.Path]::GetFileName($shellPath) -eq "bash.exe") {
        $historyArguments = @("--noprofile", "--norc", "-c", "export PATH=/usr/bin:/bin:`$PATH; sh ./.githooks/check-history")
    }
    $historyOutput = @(& $shellPath @historyArguments 2>&1)
    $historyExitCode = $LASTEXITCODE
    $historyText = $historyOutput -join [Environment]::NewLine
    Assert-Contract ($historyExitCode -ne 0) "The history gate accepted a commit with a non-standard author and committer identity."
    Assert-Contract ($historyText.Contains("author and committer must be KeelMatrix <keelmatrix@gmail.com>.", [StringComparison]::Ordinal)) "The history gate did not report the required identity rule. Output: $historyText"
    Write-Host "Identity check: non-standard author and committer rejected."

    $completed = $true
}
finally {
    if ($pushedLocation) {
        Pop-Location
    }
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
    $timer.Stop()
    $status = if ($completed) { "passed" } else { "failed" }
    Write-Host ("History gate summary: status={0}; positive={1}; negative={2}; matrix={3}; matrixAccepted={4}; time={5:N3}s; timeouts={6}." -f $status, $positiveCount, $negativeCount, $matrixCount, $matrixAcceptedCount, $timer.Elapsed.TotalSeconds, $timeoutCount)
}
