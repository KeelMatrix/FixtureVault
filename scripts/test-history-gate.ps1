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
$identityPositiveCount = 0
$identityNegativeCount = 0

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

worker_limit=16
active_workers=0

run_case() {
  expected=$1
  message_file=$2
  case_number=$3
  mismatch_file=$4
  if timeout 5s ./.githooks/commit-msg "$message_file" >/dev/null 2>&1; then
    actual=0
  else
    actual=$?
  fi
  if [ "$actual" -ne "$expected" ]; then
    printf '%s|%s|%s\n' "$case_number" "$expected" "$actual" > "$mismatch_file.$case_number"
  fi
}

case_count=0
while IFS='|' read -r expected message_file case_number; do
  if [ -z "$message_file" ]; then
    continue
  fi
  case_count=$((case_count + 1))
  run_case "$expected" "$message_file" "$case_number" "$2" &
  active_workers=$((active_workers + 1))
  if [ "$active_workers" -ge "$worker_limit" ]; then
    wait
    active_workers=0
  fi
done < "$1"
wait
printf '%s\n' "$case_count"
'@
        [IO.File]::WriteAllText($runnerPath, $runner, [Text.Encoding]::ASCII)
    }

    $mismatchName = "$BatchName-mismatches.txt"
    $mismatchPath = Join-Path $temporaryRoot $mismatchName
    [IO.File]::WriteAllText($mismatchPath, "", [Text.Encoding]::ASCII)
    $command = "export PATH=/usr/bin:/bin:`$PATH; sh $runnerName $inputName $mismatchName"
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
    $finished = $process.WaitForExit(45000)
    if (-not $finished) {
        $process.Kill()
        $process.WaitForExit()
        throw "$BatchName case runner exceeded its 45-second batch timeout."
    }

    $output = $process.StandardOutput.ReadToEnd()
    $errorOutput = $process.StandardError.ReadToEnd()
    $exitCode = $process.ExitCode
    Assert-Contract ($exitCode -eq 0) "$BatchName case runner failed with exit $exitCode. Output: $output $errorOutput"

    $reportedCount = [int]$output.Trim()
    Assert-Contract ($reportedCount -eq $Cases.Count) "$BatchName returned $reportedCount launched cases for $($Cases.Count) cases."
    $actualByCase = @{}
    $mismatches = [System.Collections.Generic.List[string]]::new()
    $mismatchFiles = @(Get-ChildItem -LiteralPath $temporaryRoot -File -Filter "$mismatchName.*")
    foreach ($mismatchFile in $mismatchFiles) {
        foreach ($line in [IO.File]::ReadAllLines($mismatchFile.FullName)) {
            if ($line -eq "") {
                continue
            }
            $fields = $line -split "\|"
            Assert-Contract ($fields.Count -eq 3) "$BatchName case runner returned malformed output: $line"
            $caseNumber = [int]$fields[0]
            $expectedExitCode = [int]$fields[1]
            $actualExitCode = [int]$fields[2]
            $actualByCase[$caseNumber] = $actualExitCode
            if ($actualExitCode -eq 124 -or $actualExitCode -eq 137) {
                $script:timeoutCount++
            }
            $null = $mismatches.Add("case $caseNumber`: expected $expectedExitCode, got $actualExitCode")
        }
    }

    Assert-Contract ($mismatches.Count -eq 0) "$BatchName mismatches: $($mismatches -join '; ')"

    $results = [System.Collections.Generic.List[object]]::new()
    for ($index = 1; $index -le $Cases.Count; $index++) {
        $expectedExitCode = $Cases[$index - 1].ExpectedExitCode
        $actualExitCode = if ($actualByCase.ContainsKey($index)) { $actualByCase[$index] } else { $expectedExitCode }
        $null = $results.Add([pscustomobject]@{
                CaseNumber = $index
                ExpectedExitCode = $expectedExitCode
                ActualExitCode = $actualExitCode
            })
    }

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

# Keep an immutable text manifest beside the executable corpus. The manifest
# makes a removed, rewritten, or moved baseline line fail instead of relying
# on a count and a claimed zero-change report.
$previousPositiveCorpusManifest = @'
Accept SHA-1 digests from legacy manifests
Use SHA-224 for compatibility vectors
Fix SHA-256 hashing
Switch cache keys to SHA-384
Retain SHA-512 integrity checks
Parse SHA-3 digest labels
Remove MD5 from the default integrity policy
Compare HMAC-256 signatures in fixture metadata
Preserve UTF-8 BOM handling in reports
Decode UTF-16 fixture files with a byte-order mark
Keep UTF-32 metadata round trips deterministic
Verify UTF-16LE/BE and UTF-32LE/BE baseline decoding
Preserve UTF-16/UTF-32 BOM detection
Reject invalid Unicode surrogate pairs
Normalize NFC filenames before comparison
Handle Latin-1 fixture input explicitly
Support HTTP-2 request fixtures
Add HTTP-3 protocol coverage
Require TLS-1 for legacy endpoint tests
Upgrade TLS-1.1 negotiation checks
Retain TLS-1.2 compatibility coverage
Upgrade TLS-1.3 support
Reject SSL-3 fallback
Parse RFC-9110 headers
Apply RFC-2119 requirement wording
Normalize ISO-8601 timestamps
Preserve IEEE-754 float round trips
Read ECMA-335 metadata tokens
Cover AES-128 encrypted fixtures
Cover AES-256 encrypted fixtures
Validate RSA-2048 key metadata
Parse MIME-1 multipart boundaries
Record CVE-2026-1234 advisory metadata
Keep Git worktree paths repository relative
Preserve merge-base detection for shallow clones
Ignore untracked fixture outputs
Handle detached HEAD during package smoke
Run Linux and Windows fixture checks
Cache NuGet restore packages in CI
Fail CI on malformed policy input
Publish test results after scan failures
Retry transient restore metadata reads
Normalize Windows path separators
Guard Unix symlink traversal
Reject relative path segments
Preserve Unicode filenames on macOS
Detect case collisions on NTFS
Keep CRLF content stable across platforms
Add KeelMatrix.FixtureVault package metadata
Include README and license in the nupkg
Verify SourceLink commit metadata
Pack the net8.0 tool command
Validate nuspec repository URL
Keep snupkg symbols beside the package
Parse SemVer 2.0 prerelease labels
Reject invalid version ranges
Align package and tool versions
Compare major minor patch components
Document version 0.1.0 defaults
Bound fixture enumeration memory
Avoid repeated UTF-8 allocations
Hash large files in a single pass
Measure scan throughput on cold disk
Skip duplicate directory stats
Return exit code 2 for configuration errors
Keep malformed JSON diagnostics concise
Fail closed when content is uninspectable
Do not echo secret values in errors
Preserve actionable remediation text
Add FV007 sensitive-data diagnostics
Document FV-E016 uninspectable content errors
Keep net8.0 tool startup deterministic
Guard case-insensitive extension matching
Report unsupported fixture conventions
Read policy files without mutation
Keep JSON report schema versioned
Separate console and JSON renderers
Use bounded file-size checks
Handle empty fixture roots gracefully
Verify no fixture bytes leave the process
Retain stable rule ordering
Make scan output reproducible
Use UTF-8 and UTF-16 encodings
Preserve UTF-16LE byte order
Preserve UTF-32BE byte order
Hash with SHA-1
Hash with SHA-256
Hash with SHA-512
Validate MD5-5 compatibility
Negotiate HTTP-2
Negotiate TLS-1.2
Parse RFC-9110 metadata
Apply ISO-8601 timestamps
Read IEEE-754 values
Inspect ECMA-335 metadata
Encrypt with AES-256
Authenticate with HMAC-256
Load RSA-2048 keys
Parse MIME-1 content
Track CVE-2021-44228 advisories
Run the net8.0 tool
Report FV007 findings
Report FV-E016 errors
Skip FV-SKIP-ENCODING diagnostics
'@ -split "`r?`n" | Where-Object { $_ -ne "" }
Assert-Contract ($previousPositiveCorpusManifest.Count -eq $previousPositiveCorpus.Count) "The positive corpus manifest count changed."

$corpusMoves = [System.Collections.Generic.List[string]]::new()
$corpusRewrites = [System.Collections.Generic.List[string]]::new()
$corpusRemovals = [System.Collections.Generic.List[string]]::new()
$corpusAdditions = [System.Collections.Generic.List[string]]::new()
for ($index = 0; $index -lt $previousPositiveCorpusManifest.Count; $index++) {
    $expectedLine = $previousPositiveCorpusManifest[$index]
    $actualLine = $previousPositiveCorpus[$index]
    if ($actualLine -eq $expectedLine) {
        continue
    }
    $actualIndex = [Array]::IndexOf($previousPositiveCorpusManifest, $actualLine)
    $expectedIndex = [Array]::IndexOf($previousPositiveCorpus, $expectedLine)
    if ($actualIndex -ge 0 -and $expectedIndex -ge 0) {
        $null = $corpusMoves.Add("'$actualLine' moved from baseline position $($actualIndex + 1) to current position $($index + 1)")
    }
    else {
        $null = $corpusRewrites.Add("baseline $($index + 1): '$expectedLine' -> '$actualLine'")
    }
}
foreach ($line in $previousPositiveCorpusManifest) {
    if (-not $previousPositiveCorpus.Contains($line)) {
        $null = $corpusRemovals.Add($line)
    }
}
foreach ($line in $previousPositiveCorpus) {
    if (-not $previousPositiveCorpusManifest.Contains($line)) {
        $null = $corpusAdditions.Add($line)
    }
}
$corpusIntegrity = [pscustomobject]@{
    Preserved = $previousPositiveCorpus.Count - $corpusMoves.Count - $corpusRewrites.Count - $corpusRemovals.Count
    Moved = $corpusMoves.Count
    Rewritten = $corpusRewrites.Count
    Removed = $corpusRemovals.Count
    Added = $corpusAdditions.Count
}
Assert-Contract (($corpusIntegrity.Moved + $corpusIntegrity.Rewritten + $corpusIntegrity.Removed) -eq 0) "The preserved positive corpus changed. Moves: $($corpusMoves -join '; '); rewrites: $($corpusRewrites -join '; '); removals: $($corpusRemovals -join '; ')"

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
$bodyLabelLineBreaks = @("`n", "`r", "`r`n")
foreach ($lineBreak in $bodyLabelLineBreaks) {
    Add-TestCase -List $positiveCases -Name "positive-body-label-$($lineBreak.Length)" -Body ("Agent: parser role" + $lineBreak + "Continue ordinary body text") -ExpectedExitCode 0
}
$candidateMessageShape = "fix: close FV007 and release gates`n`nKeep credential ownership local and validate package and history contracts before publication."
Add-TestCase -List $positiveCases -Name "release-message-shape" -Body $candidateMessageShape -ExpectedExitCode 0

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
$matrixLayouts = @($negativeLayouts)
$matrixSuffixes = @("", "1", "12345678")

foreach ($prefix in $internalPrefixes) {
    Add-TestCase -List $negativeCases -Name "prefix-alone-$prefix" -Body $prefix -ExpectedExitCode 1
    foreach ($layout in $negativeLayouts) {
        Add-TestCase -List $negativeCases -Name "prefix-whitespace-$prefix-$($layout.Name)" -Body ($prefix + $layout.Before + "3" + $layout.After) -ExpectedExitCode 1
    }
    foreach ($separator in $matrixSeparators) {
        foreach ($suffix in $matrixSuffixes) {
            foreach ($layout in $matrixLayouts) {
                $message = $prefix + $layout.Before + $separator + $layout.After + $suffix
                Add-TestCase -List $matrixCases -Name "matrix-$prefix-$($layout.Name)-$suffix-$separator" -Body $message -ExpectedExitCode 1
            }
        }
    }
    foreach ($layout in $negativeLayouts) {
        $keywordBefore = if ($layout.Name -eq "direct") { " " } else { $layout.Before }
        $keywordAfter = if ($layout.Name -eq "direct") { " " } else { $layout.After }
        Add-TestCase -List $negativeCases -Name "keyword-led-reference-$prefix-$($layout.Name)" -Body ("refs" + $keywordBefore + $prefix + $keywordAfter + "-3") -ExpectedExitCode 1
    }
}

$internalVocabulary = @("paperclip", "codex", "chatgpt", "openai", "claude", "copilot", "gemini", "anthropic", "orchestration", "orchestrator")
foreach ($word in $internalVocabulary) {
    Add-TestCase -List $negativeCases -Name "vocabulary-$word" -Body $word -ExpectedExitCode 1
    Add-TestCase -List $negativeCases -Name "vocabulary-case-$word" -Body ("Fix " + $word.ToUpperInvariant() + " metadata") -ExpectedExitCode 1
    foreach ($layout in $negativeLayouts) {
        $wordBefore = if ($layout.Name -eq "direct") { " " } else { $layout.Before }
        $wordAfter = if ($layout.Name -eq "direct") { " " } else { $layout.After }
        Add-TestCase -List $negativeCases -Name "vocabulary-boundary-$word-$($layout.Name)" -Body ("Fix" + $wordBefore + $word + $wordAfter + "metadata") -ExpectedExitCode 1
    }
}

$trailerLabels = @("co-authored-by", "signed-off-by", "reviewed-by", "tested-by", "acked-by", "reported-by", "approved-by", "agent", "model", "prompt")
$trailerLayouts = @("`n", "`r", "`r`n")
$trailerValueLayouts = @(" ", "  ", "`t", " ", "`f", "`v", "—")
foreach ($label in $trailerLabels) {
    foreach ($lineBreak in $trailerLayouts) {
        foreach ($valueLayout in $trailerValueLayouts) {
            Add-TestCase -List $negativeCases -Name "trailer-$label-$($lineBreak.Length)-$($valueLayout.Length)" -Body ("Update parser" + $lineBreak + $label + ":" + $valueLayout + "Example <example@example.com>") -ExpectedExitCode 1
        }
    }
}

$phraseLayouts = @(" ", "  ", "`n", "`r", "`r`n", "`t", " ", "`f", "`v")
$reviewPhrases = @("frontier", "review round", "review pass", "acceptance round", "acceptance pass")
foreach ($phrase in $reviewPhrases) {
    foreach ($layout in $phraseLayouts) {
        $phraseBody = if ($phrase -eq "frontier") { "Use" + $layout + $phrase + $layout + "checks" } else { "Use" + $layout + $phrase.Replace(" ", $layout) + $layout + "checks" }
        Add-TestCase -List $negativeCases -Name "review-$($phrase.Replace(' ', '-'))-$($layout.Length)" -Body $phraseBody -ExpectedExitCode 1
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
    Assert-Contract ($positiveCount -eq $positiveCases.Count) "Positive corpus result count did not match the corpus and targeted regressions."

    $negativeResult = Invoke-CaseBatch -Cases $negativeCases -BatchName "negative"
    $negativeCount = $negativeResult.Count

    $matrixResult = Invoke-CaseBatch -Cases $matrixCases -BatchName "matrix"
    $matrixCount = $matrixResult.Count
    $acceptedCells = @($matrixResult.Results | Where-Object { $_.ActualExitCode -eq 0 })
    $matrixAcceptedCount = $acceptedCells.Count
    Assert-Contract ($matrixAcceptedCount -eq 0) "The generated internal-prefix matrix accepted $matrixAcceptedCount cells."

    Write-Host "Positive corpus: preserved=$($previousPositiveCorpus.Count), added=$($additionalPositiveCorpus.Count), targeted=$($positiveCases.Count - $positiveCorpus.Count), corpus=$($positiveCorpus.Count), total=$positiveCount, all exit 0."
    Write-Host "Negative corpus: cases=$negativeCount, all exit 1."
    Write-Host "Generated matrix: cells=$matrixCount, accepted-cell list:"
    Write-Host "  (empty)"
    Write-Host "Corpus integrity: preserved=$($corpusIntegrity.Preserved); moved=$($corpusIntegrity.Moved); rewritten=$($corpusIntegrity.Rewritten); removed=$($corpusIntegrity.Removed); baseline-added=$($corpusIntegrity.Added); corpus-added=$($additionalPositiveCorpus.Count)."
    Write-Host "Corpus moves: $($(if ($corpusMoves.Count -eq 0) { '(none)' } else { $corpusMoves -join '; ' }))"

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

    function Invoke-HistoryGuard {
        param(
            [Parameter(Mandatory = $true)]
            [string]$WorkingDirectory
        )

        Push-Location $WorkingDirectory
        try {
            $guardOutput = @(& $shellPath @historyArguments 2>&1)
            $guardExitCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
        }

        [pscustomobject]@{
            ExitCode = $guardExitCode
            Output = $guardOutput
        }
    }

    $repositoryUri = ([Uri]::new($repositoryRoot)).AbsoluteUri
    $historySource = Join-Path $temporaryRoot "history-source"
    $sourceCloneResult = Invoke-Git @("clone", $repositoryUri, $historySource)
    Assert-Contract ($sourceCloneResult.ExitCode -eq 0) "Could not create the disposable history source clone: $($sourceCloneResult.Output -join [Environment]::NewLine)"
    Copy-Item -Path (Join-Path $repositoryRoot ".githooks\*") -Destination (Join-Path $historySource ".githooks") -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $repositoryRoot ".gitattributes") -Destination (Join-Path $historySource ".gitattributes") -Force
    $stageSourceResult = Invoke-Git @("-C", $historySource, "add", "--", ".gitattributes", ".githooks")
    Assert-Contract ($stageSourceResult.ExitCode -eq 0) "Could not stage the current history guard in the disposable source clone: $($stageSourceResult.Output -join [Environment]::NewLine)"
    $sourceCommitResult = Invoke-Git @("-C", $historySource, "-c", "user.name=KeelMatrix", "-c", "user.email=keelmatrix@gmail.com", "commit", "--allow-empty", "-m", "Exercise shallow history guard")
    Assert-Contract ($sourceCommitResult.ExitCode -eq 0) "Could not commit the current history guard into the disposable source clone: $($sourceCommitResult.Output -join [Environment]::NewLine)"
    $historySourceUri = ([Uri]::new($historySource)).AbsoluteUri
    foreach ($depth in @(1, 2)) {
        $shallowRoot = Join-Path $temporaryRoot ("shallow-depth-" + $depth)
        $cloneTimer = [Diagnostics.Stopwatch]::StartNew()
        $cloneResult = Invoke-Git @("-c", "core.autocrlf=true", "clone", "--depth", $depth, $historySourceUri, $shallowRoot)
        $cloneTimer.Stop()
        Assert-Contract ($cloneResult.ExitCode -eq 0) "Could not create the depth-$depth disposable shallow clone: $($cloneResult.Output -join [Environment]::NewLine)"

        foreach ($hookName in @("check-history", "commit-msg")) {
            $hookBytes = [IO.File]::ReadAllText((Join-Path $shallowRoot (Join-Path ".githooks" $hookName)))
            Assert-Contract (-not $hookBytes.Contains("`r`n", [StringComparison]::Ordinal)) "The depth-$depth clone checked out .githooks/$hookName with CRLF under core.autocrlf=true."
        }

        $shallowState = Invoke-Git @("-C", $shallowRoot, "rev-parse", "--is-shallow-repository")
        Assert-Contract ($shallowState.ExitCode -eq 0 -and $shallowState.Output.Trim() -eq "true") "The depth-$depth disposable clone did not report shallow Git state: $($shallowState.Output -join [Environment]::NewLine)"

        $guardTimer = [Diagnostics.Stopwatch]::StartNew()
        $shallowResult = Invoke-HistoryGuard -WorkingDirectory $shallowRoot
        $guardTimer.Stop()
        $shallowText = $shallowResult.Output -join [Environment]::NewLine
        Assert-Contract ($shallowResult.ExitCode -ne 0) "The history guard accepted a depth-$depth shallow clone. Output: $shallowText"
        Assert-Contract ($shallowText.Contains("HISTORY_GUARD=FAIL reason=shallow-history", [StringComparison]::Ordinal)) "The depth-$depth shallow history failure did not identify the missing complete history. Output: $shallowText"
        Write-Host ("Shallow clone depth={0}: guard exit={1}, failure message matched; clone={2:N3}s, guard={3:N3}s." -f $depth, $shallowResult.ExitCode, $cloneTimer.Elapsed.TotalSeconds, $guardTimer.Elapsed.TotalSeconds)

        $unshallowTimer = [Diagnostics.Stopwatch]::StartNew()
        $unshallowResult = Invoke-Git @("-C", $shallowRoot, "fetch", "--unshallow")
        $unshallowTimer.Stop()
        Assert-Contract ($unshallowResult.ExitCode -eq 0) "Could not unshallow the depth-$depth disposable clone: $($unshallowResult.Output -join [Environment]::NewLine)"
        $unshallowedState = Invoke-Git @("-C", $shallowRoot, "rev-parse", "--is-shallow-repository")
        Assert-Contract ($unshallowedState.ExitCode -eq 0 -and $unshallowedState.Output.Trim() -eq "false") "The unshallowed depth-$depth clone did not report complete Git state: $($unshallowedState.Output -join [Environment]::NewLine)"
        $unshallowedResult = Invoke-HistoryGuard -WorkingDirectory $shallowRoot
        Assert-Contract ($unshallowedResult.ExitCode -eq 0) "The history guard rejected the unshallowed depth-$depth clone: $($unshallowedResult.Output -join [Environment]::NewLine)"
        $unshallowedText = $unshallowedResult.Output -join [Environment]::NewLine
        Assert-Contract ($unshallowedText.Contains("HISTORY_GUARD=PASS reason=complete-history", [StringComparison]::Ordinal)) "The unshallowed depth-$depth clone did not report a machine-readable pass. Output: $unshallowedText"
        Write-Host ("Unshallow control depth={0}: guard exit=0; fetch={1:N3}s." -f $depth, $unshallowTimer.Elapsed.TotalSeconds)
    }

    $graftRoot = Join-Path $temporaryRoot "grafted-replace"
    $graftCloneResult = Invoke-Git @("-c", "core.autocrlf=true", "clone", $historySourceUri, $graftRoot)
    Assert-Contract ($graftCloneResult.ExitCode -eq 0) "Could not create the disposable graft/replace clone: $($graftCloneResult.Output -join [Environment]::NewLine)"
    $graftReplaceResult = Invoke-Git @("-C", $graftRoot, "replace", "--graft", "HEAD")
    Assert-Contract ($graftReplaceResult.ExitCode -eq 0) "Could not install the disposable graft/replace ancestry: $($graftReplaceResult.Output -join [Environment]::NewLine)"
    $graftShallowState = Invoke-Git @("-C", $graftRoot, "rev-parse", "--is-shallow-repository")
    Assert-Contract ($graftShallowState.ExitCode -eq 0 -and $graftShallowState.Output.Trim() -eq "false") "The graft/replace clone unexpectedly reported shallow Git state: $($graftShallowState.Output -join [Environment]::NewLine)"
    $graftAncestor = Invoke-Git @("-C", $graftRoot, "rev-parse", "HEAD~2")
    Assert-Contract ($graftAncestor.ExitCode -ne 0) "The graft/replace clone unexpectedly retained HEAD~2: $($graftAncestor.Output -join [Environment]::NewLine)"
    $graftGuardTimer = [Diagnostics.Stopwatch]::StartNew()
    $graftResult = Invoke-HistoryGuard -WorkingDirectory $graftRoot
    $graftGuardTimer.Stop()
    $graftText = $graftResult.Output -join [Environment]::NewLine
    Assert-Contract ($graftResult.ExitCode -ne 0) "The history guard accepted a replaced/grafted ancestry. Output: $graftText"
    Assert-Contract ($graftText.Contains("HISTORY_GUARD=FAIL reason=shallow-history", [StringComparison]::Ordinal)) "The replaced/grafted ancestry failure did not identify the incomplete history. Output: $graftText"
    Write-Host ("Graft/replace clone: shallow-state=false; HEAD~2 exit={0}; guard exit={1}; failure message matched; guard={2:N3}s." -f $graftAncestor.ExitCode, $graftResult.ExitCode, $graftGuardTimer.Elapsed.TotalSeconds)

    $historyOutput = @(& $shellPath @historyArguments 2>&1)
    $historyExitCode = $LASTEXITCODE
    $historyText = $historyOutput -join [Environment]::NewLine
    Assert-Contract ($historyExitCode -ne 0) "The history gate accepted a commit with a non-standard author and committer identity."
    Assert-Contract ($historyText.Contains("author is not an approved KeelMatrix or Dependabot identity.", [StringComparison]::Ordinal)) "The history gate did not report the author allowlist failure. Output: $historyText"
    Write-Host "Identity check: non-standard author and committer rejected."

    $identityCases = @(
        [pscustomobject]@{ Name = "keelmatrix-keelmatrix"; AuthorName = "KeelMatrix"; AuthorEmail = "keelmatrix@gmail.com"; CommitterName = "KeelMatrix"; CommitterEmail = "keelmatrix@gmail.com"; ExpectedExit = 0; Message = "Create maintenance history" },
        [pscustomobject]@{ Name = "keelmatrix-github"; AuthorName = "KeelMatrix"; AuthorEmail = "keelmatrix@gmail.com"; CommitterName = "GitHub"; CommitterEmail = "noreply@github.com"; ExpectedExit = 0; Message = "Create web maintenance history" },
        [pscustomobject]@{ Name = "keelmatrix-dependabot"; AuthorName = "KeelMatrix"; AuthorEmail = "keelmatrix@gmail.com"; CommitterName = "dependabot[bot]"; CommitterEmail = "49699333+dependabot[bot]@users.noreply.github.com"; ExpectedExit = 0; Message = "Create automated maintenance history" },
        [pscustomobject]@{ Name = "dependabot-keelmatrix"; AuthorName = "dependabot[bot]"; AuthorEmail = "49699333+dependabot[bot]@users.noreply.github.com"; CommitterName = "KeelMatrix"; CommitterEmail = "keelmatrix@gmail.com"; ExpectedExit = 0; Message = "Create dependency maintenance history" },
        [pscustomobject]@{ Name = "dependabot-github"; AuthorName = "dependabot[bot]"; AuthorEmail = "49699333+dependabot[bot]@users.noreply.github.com"; CommitterName = "GitHub"; CommitterEmail = "noreply@github.com"; ExpectedExit = 0; Message = "Create web dependency maintenance history" },
        [pscustomobject]@{ Name = "dependabot-dependabot"; AuthorName = "dependabot[bot]"; AuthorEmail = "49699333+dependabot[bot]@users.noreply.github.com"; CommitterName = "dependabot[bot]"; CommitterEmail = "49699333+dependabot[bot]@users.noreply.github.com"; ExpectedExit = 0; Message = "Create dependency bot history" },
        [pscustomobject]@{ Name = "unknown-author"; AuthorName = "Example Author"; AuthorEmail = "example.author@example.com"; CommitterName = "KeelMatrix"; CommitterEmail = "keelmatrix@gmail.com"; ExpectedExit = 1; Message = "Create unauthorized history" },
        [pscustomobject]@{ Name = "unknown-committer"; AuthorName = "KeelMatrix"; AuthorEmail = "keelmatrix@gmail.com"; CommitterName = "Example Committer"; CommitterEmail = "example.committer@example.com"; ExpectedExit = 1; Message = "Create unauthorized committer history" },
        [pscustomobject]@{ Name = "coauthor-trailer"; AuthorName = "KeelMatrix"; AuthorEmail = "keelmatrix@gmail.com"; CommitterName = "KeelMatrix"; CommitterEmail = "keelmatrix@gmail.com"; ExpectedExit = 1; Message = "Maintenance with forbidden trailer`n`nCo-authored-by: Agent <agent@example.com>" },
        [pscustomobject]@{ Name = "internal-metadata"; AuthorName = "KeelMatrix"; AuthorEmail = "keelmatrix@gmail.com"; CommitterName = "KeelMatrix"; CommitterEmail = "keelmatrix@gmail.com"; ExpectedExit = 1; Message = "$($internalPrefixes[0])-999 maintenance metadata" }
    )

    foreach ($identityCase in $identityCases) {
        $identityRoot = Join-Path $temporaryRoot ("identity-" + $identityCase.Name)
        New-Item -ItemType Directory -Force -Path $identityRoot | Out-Null
        Copy-Item -LiteralPath (Join-Path $repositoryRoot ".githooks") -Destination (Join-Path $identityRoot ".githooks") -Recurse
        Push-Location $identityRoot
        $previousIdentityEnvironment = @{
            GIT_AUTHOR_NAME = $env:GIT_AUTHOR_NAME
            GIT_AUTHOR_EMAIL = $env:GIT_AUTHOR_EMAIL
            GIT_COMMITTER_NAME = $env:GIT_COMMITTER_NAME
            GIT_COMMITTER_EMAIL = $env:GIT_COMMITTER_EMAIL
        }
        try {
            $null = Invoke-Git @("init", "--quiet")
            $null = Invoke-Git @("config", "user.name", $identityCase.AuthorName)
            $null = Invoke-Git @("config", "user.email", $identityCase.AuthorEmail)
            $env:GIT_AUTHOR_NAME = $identityCase.AuthorName
            $env:GIT_AUTHOR_EMAIL = $identityCase.AuthorEmail
            $env:GIT_COMMITTER_NAME = $identityCase.CommitterName
            $env:GIT_COMMITTER_EMAIL = $identityCase.CommitterEmail
            $created = Invoke-Git @("commit", "--allow-empty", "-m", $identityCase.Message)
            Assert-Contract ($created.ExitCode -eq 0) "Could not create identity case $($identityCase.Name): $($created.Output -join [Environment]::NewLine)"
            $identityOutput = @(& $shellPath @historyArguments 2>&1)
            $identityExit = $LASTEXITCODE
            Assert-Contract ($identityExit -eq $identityCase.ExpectedExit) "Identity case $($identityCase.Name) returned $identityExit instead of $($identityCase.ExpectedExit): $($identityOutput -join [Environment]::NewLine)"
            if ($identityCase.ExpectedExit -eq 0) {
                $identityPositiveCount++
            }
            else {
                $identityNegativeCount++
            }
        }
        finally {
            foreach ($environmentName in $previousIdentityEnvironment.Keys) {
                if ($null -eq $previousIdentityEnvironment[$environmentName]) {
                    Remove-Item "Env:$environmentName" -ErrorAction SilentlyContinue
                }
                else {
                    Set-Item "Env:$environmentName" $previousIdentityEnvironment[$environmentName]
                }
            }
            Pop-Location
        }
    }
    Write-Host "Identity allowlist corpus: positive=$identityPositiveCount; negative=$identityNegativeCount; GitHub web-flow and Dependabot combinations verified."

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
    Write-Host ("History gate summary: status={0}; positive={1}; negative={2}; matrix={3}; matrixAccepted={4}; identityPositive={5}; identityNegative={6}; time={7:N3}s; timeouts={8}." -f $status, $positiveCount, $negativeCount, $matrixCount, $matrixAcceptedCount, $identityPositiveCount, $identityNegativeCount, $timer.Elapsed.TotalSeconds, $timeoutCount)
}
