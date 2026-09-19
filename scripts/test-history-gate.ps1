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

function ConvertTo-OctalEscapedUtf8 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $builder = [Text.StringBuilder]::new()
    foreach ($byte in [Text.Encoding]::UTF8.GetBytes($Value)) {
        [void]$builder.Append(('\' + [Convert]::ToString([int]$byte, 8).PadLeft(3, '0')))
    }
    $builder.ToString()
}

function Invoke-TechnicalTailMatrix {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Messages
    )

    $matrixPath = Join-Path $temporaryRoot "technical-tail-matrix.txt"
    $encodedMessages = foreach ($message in $Messages) {
        ConvertTo-OctalEscapedUtf8 -Value $message
    }
    [IO.File]::WriteAllLines($matrixPath, $encodedMessages, [Text.UTF8Encoding]::new($false))
    $shellArguments = @("-c", "export PATH=/usr/bin:/bin:`$PATH; ./.githooks/commit-msg --technical-tail-matrix technical-tail-matrix.txt")
    if ([IO.Path]::GetFileName($shellPath) -eq "bash.exe") {
        $shellArguments = @("--noprofile", "--norc", "-c", "export PATH=/usr/bin:/bin:`$PATH; ./.githooks/commit-msg --technical-tail-matrix technical-tail-matrix.txt")
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

    $taskPrefix = -join ([char[]](75, 69, 69))
    $genericPrefix = -join ([char[]](65, 66, 67))
    $taskReference = $taskPrefix + "-3"
    $genericReference = $genericPrefix + "-1"
    $secondaryTaskReference = $taskPrefix + "-589"
    $genericTwelveReference = $genericPrefix + "-12"
    $otherReference = (-join ([char[]](88, 89, 90))) + "-34"
    $longGenericReference = $genericPrefix + "-12345678"
    $reviewWord = -join ([char[]](102, 114, 111, 110, 116, 105, 101, 114))

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

    $negativeCorpus = @(
        $taskReference,
        $genericReference,
        $secondaryTaskReference,
        "Refs $genericTwelveReference",
        "Refs $secondaryTaskReference",
        "Closes $genericTwelveReference",
        "Fixes $otherReference",
        "Part of $taskReference",
        "[$taskReference]",
        "($genericTwelveReference)",
        "issue: $taskReference",
        "task #$genericReference",
        "related to $secondaryTaskReference",
        "reopens $taskReference",
        $longGenericReference,
        ($reviewWord + " review"),
        $reviewWord,
        "rejection round",
        "review round",
        "acceptance pass"
    )

    $tokenDecisionTable = @(
        @{ Message = $taskReference; Expected = 1 }
        @{ Message = $genericReference; Expected = 1 }
        @{ Message = $secondaryTaskReference; Expected = 1 }
        @{ Message = "Refs $secondaryTaskReference"; Expected = 1 }
        @{ Message = "closes $genericTwelveReference"; Expected = 1 }
        @{ Message = "[$taskReference]"; Expected = 1 }
        @{ Message = "($taskReference)"; Expected = 1 }
        @{ Message = "Fixes $taskReference"; Expected = 1 }
        @{ Message = "Part of $taskReference"; Expected = 1 }
        @{ Message = "UTF-8-1"; Expected = 1 }
        @{ Message = "UTF-16-1"; Expected = 1 }
        @{ Message = "SHA-256-1"; Expected = 1 }
        @{ Message = "HTTP-2-1"; Expected = 1 }
        @{ Message = "TLS-1.2-1"; Expected = 1 }
        @{ Message = "NET-8-1"; Expected = 1 }
        @{ Message = "FV007-1"; Expected = 1 }
        @{ Message = "UTF-8-123"; Expected = 1 }
        @{ Message = "SHA-256-42"; Expected = 1 }
        @{ Message = $reviewWord; Expected = 1 }
        @{ Message = ($reviewWord + " review"); Expected = 1 }
        @{ Message = "rejection round"; Expected = 1 }
        @{ Message = "review round"; Expected = 1 }
        @{ Message = "acceptance pass"; Expected = 1 }
        @{ Message = "UTF-8"; Expected = 0 }
        @{ Message = "UTF-16"; Expected = 0 }
        @{ Message = "UTF-32"; Expected = 0 }
        @{ Message = "LATIN-1"; Expected = 0 }
        @{ Message = "SHA-1"; Expected = 0 }
        @{ Message = "SHA-256"; Expected = 0 }
        @{ Message = "SHA-384"; Expected = 0 }
        @{ Message = "SHA-512"; Expected = 0 }
        @{ Message = "MD5-5"; Expected = 0 }
        @{ Message = "HTTP-2"; Expected = 0 }
        @{ Message = "HTTP-3"; Expected = 0 }
        @{ Message = "TLS-1.2"; Expected = 0 }
        @{ Message = "TLS-1.3"; Expected = 0 }
        @{ Message = "SSL-3"; Expected = 0 }
        @{ Message = "RFC-9110"; Expected = 0 }
        @{ Message = "RFC-2119"; Expected = 0 }
        @{ Message = "ISO-8601"; Expected = 0 }
        @{ Message = "IEEE-754"; Expected = 0 }
        @{ Message = "ECMA-335"; Expected = 0 }
        @{ Message = "AES-256"; Expected = 0 }
        @{ Message = "HMAC-256"; Expected = 0 }
        @{ Message = "RSA-2048"; Expected = 0 }
        @{ Message = "MIME-1"; Expected = 0 }
        @{ Message = "CVE-2021-44228"; Expected = 0 }
        @{ Message = "net8.0"; Expected = 0 }
        @{ Message = "net10.0"; Expected = 0 }
        @{ Message = "FV007"; Expected = 0 }
        @{ Message = "FV-E016"; Expected = 0 }
        @{ Message = "SHA-256 hashing"; Expected = 0 }
        @{ Message = "Upgrade TLS-1.3 support"; Expected = 0 }
        @{ Message = "Parse RFC-9110 headers"; Expected = 0 }
        @{ Message = "Fix UTF-8 decoding"; Expected = 0 }
        @{ Message = "Add AES-256-GCM support"; Expected = 0 }
        @{ Message = "Support HTTP-2 and HTTP-3"; Expected = 0 }
        @{ Message = "ISO-8601-1"; Expected = 1 }
        @{ Message = "IEEE-754-2019"; Expected = 1 }
    )

    $additionalTokenCases = @(
        @{ Message = "UTF-8-1"; Expected = 1 }
        @{ Message = "UTF-8-1.2"; Expected = 1 }
        @{ Message = "UTF-8-GCM"; Expected = 1 }
        @{ Message = "UTF-8-HMAC"; Expected = 1 }
        @{ Message = "UTF-8-CBC"; Expected = 1 }
        @{ Message = "UTF-8-LE"; Expected = 1 }
        @{ Message = "UTF-8-BE"; Expected = 1 }
        @{ Message = "UTF-8-BOM"; Expected = 1 }
        @{ Message = "LATIN-1-1"; Expected = 1 }
        @{ Message = "LATIN-1-1.2"; Expected = 1 }
        @{ Message = "LATIN-1-GCM"; Expected = 1 }
        @{ Message = "SHA-256-1.2"; Expected = 1 }
        @{ Message = "SHA-256-GCM"; Expected = 1 }
        @{ Message = "SHA-256-HMAC"; Expected = 1 }
        @{ Message = "SHA-256-CBC"; Expected = 1 }
        @{ Message = "MD5-5-1"; Expected = 1 }
        @{ Message = "MD5-5-1.2"; Expected = 1 }
        @{ Message = "HTTP-2-1.2"; Expected = 1 }
        @{ Message = "HTTP-2-ALPN"; Expected = 1 }
        @{ Message = "TLS-1.2-1"; Expected = 1 }
        @{ Message = "TLS-1.2-1.3"; Expected = 1 }
        @{ Message = "TLS-1.2-GCM"; Expected = 1 }
        @{ Message = "TLS-1.2-HMAC"; Expected = 1 }
        @{ Message = "TLS-1.2-CBC"; Expected = 1 }
        @{ Message = "TLS-1.2-ALPN"; Expected = 1 }
        @{ Message = "SSL-3-1"; Expected = 1 }
        @{ Message = "SSL-3-1.2"; Expected = 1 }
        @{ Message = "SSL-3-ALPN"; Expected = 1 }
        @{ Message = "RFC-9110-1"; Expected = 1 }
        @{ Message = "RFC-9110-1.2"; Expected = 1 }
        @{ Message = "ISO-8601-1"; Expected = 1 }
        @{ Message = "ISO-8601-1.2"; Expected = 1 }
        @{ Message = "IEEE-754-2019"; Expected = 1 }
        @{ Message = "IEEE-754-2019.1"; Expected = 1 }
        @{ Message = "ECMA-335-1"; Expected = 1 }
        @{ Message = "ECMA-335-1.2"; Expected = 1 }
        @{ Message = "ECMA-335-HMAC"; Expected = 1 }
        @{ Message = "AES-256-1"; Expected = 1 }
        @{ Message = "AES-256-1.2"; Expected = 1 }
        @{ Message = "AES-256-GCM"; Expected = 0 }
        @{ Message = "AES-256-CBC"; Expected = 1 }
        @{ Message = "AES-256-LE"; Expected = 1 }
        @{ Message = "AES-256-BE"; Expected = 1 }
        @{ Message = "HMAC-256-1"; Expected = 1 }
        @{ Message = "HMAC-256-1.2"; Expected = 1 }
        @{ Message = "HMAC-256-GCM"; Expected = 1 }
        @{ Message = "RSA-2048-1"; Expected = 1 }
        @{ Message = "RSA-2048-1.2"; Expected = 1 }
        @{ Message = "RSA-2048-CBC"; Expected = 1 }
        @{ Message = "MIME-1-1"; Expected = 1 }
        @{ Message = "MIME-1-1.2"; Expected = 1 }
        @{ Message = "CVE-2021-44228-1"; Expected = 1 }
        @{ Message = "CVE-2021-44228-1.2"; Expected = 1 }
        @{ Message = "NET-8-1"; Expected = 1 }
        @{ Message = "NET8.0-1"; Expected = 1 }
        @{ Message = "FV007-1"; Expected = 1 }
        @{ Message = "FV-E016-1"; Expected = 1 }
        @{ Message = "FV-SKIP-ENCODING-1"; Expected = 1 }
        @{ Message = "UTF-16LE"; Expected = 0 }
        @{ Message = "UTF-32BE"; Expected = 0 }
        @{ Message = "TLS-1"; Expected = 0 }
        @{ Message = "NET-8"; Expected = 0 }
        @{ Message = "FV-SKIP-ENCODING"; Expected = 0 }
    )

    $requiredRejectCases = @(
        "Fix UTF-8_1"
        "Fix UTF-8#1"
        "Fix UTF-8/1"
        "Fix UTF-8.1"
        "Fix UTF-8:1"
        "Fix UTF-8~1"
        "Fix UTF-8+1"
        "Fix UTF-8,1"
        "Fix UTF-8;1"
        "Fix UTF-8=1"
        "Fix UTF-8|1"
        "Fix UTF-8@1"
        "Fix UTF-8^1"
        "Fix UTF-8&1"
        "Fix UTF-8*1"
        "Fix UTF-8(1)"
        "Fix UTF-8[1]"
        "Fix UTF-8{1}"
        "Fix UTF-8<1>"
        'Fix UTF-8"1'
        "Fix UTF-8'1"
        "Fix SHA-256_1"
        "Fix SHA-256#1"
        "Fix SHA-256/1"
        "Fix SHA-256.1"
        "Fix SHA-256:1"
        "Fix TLS-1.2#1"
        "Fix TLS-1.2/1"
        "Fix TLS-1.2.1"
        "Fix NET8.0_1"
        "Fix NET8.0.1"
        "Fix NET8.0#1"
        "Fix NET8.0/1"
        "Fix NET-8_1"
        "NET-8.1"
        "NET-8.12"
        "NET-8.2019"
        "NET-8.12345678"
        "NET-8.999999999"
        "Fix FV007_1"
        "Fix FV-E016#1"
        "Fix RFC-9110_1"
        "Fix AES-256_1"
        "Fix HMAC-256#1"
        "Fix CVE-2021-44228_1"
        "UTF-8_1_2"
        "UTF-8_123"
        "Fix UTF-8`n_1"
        "UTF-8`n_1"
        "Fix UTF-8`n#1"
        "Fix UTF-8`n/1"
        "Fix UTF-8`n.1"
        "Fix SHA-256`n_1"
        "Fix TLS-1.2`n#1"
        "Fix NET8.0`n_1"
        ("Fix UTF-8" + [char]0x2014 + "_1")
        ("Fix UTF-8" + [char]0x2026 + "_1")
        ("Fix UTF-8" + [char]0xff3f + "_1")
        ($taskReference + "`n-3")
        ($genericReference + "`n-1")
        ($taskPrefix + "`n- 3")
        ($genericPrefix + "`n- 1")
    )

    $requiredAcceptCases = @(
        "UTF-8"
        "UTF-16"
        "UTF-32"
        "UTF-16LE"
        "UTF-16BE"
        "UTF-32LE"
        "UTF-32BE"
        "LATIN-1"
        "SHA-1"
        "SHA-256"
        "SHA-384"
        "SHA-512"
        "MD5-5"
        "HTTP-2"
        "HTTP-3"
        "TLS-1"
        "TLS-1.2"
        "TLS-1.3"
        "SSL-3"
        "RFC-9110"
        "RFC-2119"
        "ISO-8601"
        "IEEE-754"
        "ECMA-335"
        "AES-256"
        "AES-256-GCM"
        "HMAC-256"
        "RSA-2048"
        "MIME-1"
        "CVE-2021-44228"
        "net8.0"
        "net10.0"
        "NET-8"
        "FV007"
        "FV-E016"
        "FV-SKIP-ENCODING"
        "Fix UTF-8 decoding"
        "Add AES-256-GCM support"
        "Support HTTP-2 and HTTP-3"
        "Upgrade TLS-1.3 support"
        "Parse RFC-9110 headers"
        "Hash with SHA-256"
        "UTF-8 1"
        "UTF-8`n1"
        ("UTF-8" + [char]0x0661)
        "Fix empty credential false positives"
        "reject decoded NUL content"
        "Handle URL-encoded empty API keys"
        "Clarify received fixture content inspection"
    )

    # Generate the complete normalized-stream matrix. The finite surface covers
    # documented technical forms, no/single/repeated/surrounded joiners, all
    # required ASCII punctuation and controls, representative non-ASCII
    # separators, whitespace layouts around each separator, and every required
    # numeric suffix. Accepted cells must be only the documented pure-whitespace
    # continuations; every other generated cell is a rejection.
    $matrixTechnicalTokens = @(
        "UTF-16LE"
        "LATIN-1"
        "SHA-256"
        "TLS-1.2"
        "RFC-9110"
        "ISO-8601"
        "IEEE-754"
        "AES-256-GCM"
        "CVE-2021-44228"
        "NET8.0"
        "NET-8"
        "FV-E016"
        "FV-SKIP-ENCODING"
    )
    $matrixJoiners = @(
        [pscustomobject]@{ Name = "none"; Value = "" }
        [pscustomobject]@{ Name = "single"; Value = "-" }
        [pscustomobject]@{ Name = "repeated"; Value = "--" }
        [pscustomobject]@{ Name = "surrounded-whitespace"; Value = "  -  " }
    )
    $matrixSeparators = @(
        [pscustomobject]@{ Name = "ASCII punctuation -"; Value = "-"; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation _"; Value = "_"; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation ."; Value = "."; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation #"; Value = "#"; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation /"; Value = "/"; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation :"; Value = ":"; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation ~"; Value = "~"; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation +"; Value = "+"; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation ,"; Value = ","; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation ;"; Value = ";"; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation ="; Value = "="; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation |"; Value = "|"; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation backslash"; Value = [char]0x5c; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation @"; Value = "@"; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation ^"; Value = "^"; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation &"; Value = "&"; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation *"; Value = "*"; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation ("; Value = "("; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation )"; Value = ")"; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation ["; Value = "["; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation ]"; Value = "]"; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation {"; Value = "{"; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation }"; Value = "}"; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation <"; Value = "<"; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation >"; Value = ">"; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation double-quote"; Value = [char]0x22; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation apostrophe"; Value = [char]0x27; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII punctuation backtick"; Value = [char]0x60; Kind = "punctuation" }
        [pscustomobject]@{ Name = "ASCII space"; Value = " "; Kind = "whitespace" }
        (1..31 | ForEach-Object { [pscustomobject]@{ Name = ("ASCII control U+{0:X4}" -f $_); Value = [char]$_; Kind = if ($_ -in 9..13) { "whitespace" } else { "control" } } })
        [pscustomobject]@{ Name = "ASCII control U+007F"; Value = [char]0x7f; Kind = "control" }
        [pscustomobject]@{ Name = "NBSP"; Value = [char]0x00a0; Kind = "non-ASCII" }
        [pscustomobject]@{ Name = "en space"; Value = [char]0x2002; Kind = "non-ASCII" }
        [pscustomobject]@{ Name = "em space"; Value = [char]0x2003; Kind = "non-ASCII" }
        [pscustomobject]@{ Name = "ideographic space"; Value = [char]0x3000; Kind = "non-ASCII" }
        [pscustomobject]@{ Name = "em dash"; Value = [char]0x2014; Kind = "non-ASCII" }
        [pscustomobject]@{ Name = "ellipsis"; Value = [char]0x2026; Kind = "non-ASCII" }
        [pscustomobject]@{ Name = "full-width low line"; Value = [char]0xff3f; Kind = "non-ASCII" }
    )
    $matrixContinuations = @(
        [pscustomobject]@{ Name = "none"; Before = ""; After = "" }
        [pscustomobject]@{ Name = "LF"; Before = "`n"; After = "`n" }
        [pscustomobject]@{ Name = "CRLF"; Before = "`r`n"; After = "`r`n" }
        [pscustomobject]@{ Name = "TAB"; Before = "`t"; After = "`t" }
        [pscustomobject]@{ Name = "space"; Before = " "; After = " " }
        [pscustomobject]@{ Name = "space-run"; Before = "  "; After = "  " }
    )
    $matrixSuffixes = @(
        "1"
        "12"
        "2019"
        "1.2"
        "1-2"
        "12345678"
        "999999999"
    )
    Assert-Contract ($matrixJoiners.Count -eq 4) "The separator matrix must cover none, single, repeated, and surrounding-whitespace joiners."
    Assert-Contract ($matrixTechnicalTokens.Count -ge 12) "The separator matrix must cover at least twelve technical token families/forms."
    Assert-Contract ($matrixSeparators.Count -eq 68) "The separator matrix must cover 28 punctuation, 32 ASCII control, ASCII space, and 7 representative non-ASCII separators."
    Assert-Contract ($matrixContinuations.Count -eq 6) "The separator matrix must cover LF, CRLF, TAB, and space variants around separators."
    Assert-Contract ($matrixSuffixes.Count -eq 7) "The separator matrix must cover all required numeric suffix forms."

    $matrixMessages = [Collections.Generic.List[string]]::new()
    $matrixCells = [Collections.Generic.List[object]]::new()
    foreach ($token in $matrixTechnicalTokens) {
        foreach ($joiner in $matrixJoiners) {
            foreach ($separator in $matrixSeparators) {
                foreach ($continuation in $matrixContinuations) {
                    foreach ($suffix in $matrixSuffixes) {
                        $message = $token + $joiner.Value + $continuation.Before + $separator.Value + $continuation.After + $suffix
                        $matrixMessages.Add($message)
                        $matrixCells.Add([pscustomobject]@{
                            Message = $message
                            Token = $token
                            Joiner = $joiner.Name
                            Separator = $separator.Name
                            SeparatorKind = $separator.Kind
                            Continuation = $continuation.Name
                            Suffix = $suffix
                            Label = "token=$token; joiner=$($joiner.Name); separator=$($separator.Name); continuation=$($continuation.Name); suffix=$suffix"
                        })
                    }
                }
            }
        }
    }

    $matrixHookResult = Invoke-TechnicalTailMatrix -Messages @($matrixMessages)
    Assert-Contract ($matrixHookResult.ExitCode -eq 0) "The technical-tail matrix engine failed: $($matrixHookResult.Output -join [Environment]::NewLine)"
    $matrixOutput = @($matrixHookResult.Output | Where-Object { $_.ToString().Length -gt 0 })
    $matrixResults = [Collections.Generic.List[object]]::new()
    $matrixAccepted = [Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $matrixOutput.Count; $index++) {
        $matrixExitCode = [int]$matrixOutput[$index]
        $cell = $matrixCells[$index]
        $deliberateAccept = $cell.Joiner -eq "none" -and $cell.SeparatorKind -eq "whitespace"
        $matrixResults.Add([pscustomobject]@{
            Cell = $cell.Label
            Decision = if ($matrixExitCode -eq 0) { "accepted" } else { "rejected" }
        })
        if ($matrixExitCode -eq 0) {
            Assert-Contract $deliberateAccept "Normalized-stream matrix accepted an undocumented cell: $($cell.Label)."
            $matrixAccepted.Add($cell)
        }
        else {
            Assert-Contract (-not $deliberateAccept) "Normalized-stream matrix rejected a documented deliberate accept: $($cell.Label)."
        }
    }

    $expectedMatrixCells = $matrixTechnicalTokens.Count * $matrixJoiners.Count * $matrixSeparators.Count * $matrixContinuations.Count * $matrixSuffixes.Count
    Assert-Contract ($matrixOutput.Count -eq $expectedMatrixCells) "Normalized-stream matrix returned $($matrixOutput.Count) decisions instead of $expectedMatrixCells."
    $expectedDeliberateAccepts = @($matrixCells | Where-Object { $_.Joiner -eq "none" -and $_.SeparatorKind -eq "whitespace" }).Count
    Assert-Contract ($matrixAccepted.Count -eq $expectedDeliberateAccepts) "Normalized-stream matrix accepted $($matrixAccepted.Count) deliberate cells instead of $expectedDeliberateAccepts."
    Write-Host ("matrix-total`t{0}" -f $matrixResults.Count)
    Write-Host ("matrix-accepted-count`t{0}" -f $matrixAccepted.Count)
    Write-Host "matrix-accepted-cells`t(the complete list below; every cell is a pure ASCII-whitespace continuation with no separator character)"
    foreach ($cell in $matrixAccepted) {
        Write-Host ("matrix-accepted-cell`t{0}" -f $cell.Label)
    }

    foreach ($message in $requiredRejectCases) {
        $hookResult = Invoke-CommitMessageHook -Message $message
        Write-Host ("required-reject`t{0}`t{1}" -f $hookResult.ExitCode, $message)
        Assert-Contract ($hookResult.ExitCode -eq 1) "Required reject case was accepted '$message'. Output: $($hookResult.Output -join [Environment]::NewLine)"
    }

    foreach ($message in $requiredAcceptCases) {
        $hookResult = Invoke-CommitMessageHook -Message $message
        Write-Host ("required-accept`t{0}`t{1}" -f $hookResult.ExitCode, $message)
        Assert-Contract ($hookResult.ExitCode -eq 0) "Required accept case was rejected '$message'. Output: $($hookResult.Output -join [Environment]::NewLine)"
    }

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

    foreach ($case in $tokenDecisionTable + $additionalTokenCases) {
        $hookResult = Invoke-CommitMessageHook -Message $case.Message
        Write-Host ("token`t{0}`t{1}`texpected {2}" -f $hookResult.ExitCode, $case.Message, $case.Expected)
        Assert-Contract ($hookResult.ExitCode -eq $case.Expected) "Token decision table mismatch for '$($case.Message)': expected $($case.Expected), got $($hookResult.ExitCode). Output: $($hookResult.Output -join [Environment]::NewLine)"
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
