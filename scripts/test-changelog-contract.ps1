[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion,

    [string]$ChangelogPath = "CHANGELOG.md",

    [string]$ExpectedPackageVersion,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedCommit,

    [string]$RepositoryRoot = (Join-Path $PSScriptRoot "..")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Fail-Contract {
    param([string]$Message)

    throw "Changelog/version contract failed: $Message"
}

function Assert-Contract {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        Fail-Contract $Message
    }
}

function Normalize-Version {
    param([string]$Value)

    $normalized = $Value.Trim()
    if ($normalized.StartsWith("[") -and $normalized.EndsWith("]")) {
        $normalized = $normalized.Substring(1, $normalized.Length - 2).Trim()
    }

    return $normalized
}

function Assert-VersionMatches {
    param(
        [string]$Actual,
        [string]$Expected,
        [string]$Source
    )

    Assert-Contract (-not [string]::IsNullOrWhiteSpace($Actual)) "$Source is missing."
    Assert-Contract ((Normalize-Version $Actual) -ceq (Normalize-Version $Expected)) "$Source '$Actual' does not match expected version '$Expected'."
}

function Resolve-RepositoryPath {
    param([string]$Path)

    $candidate = if ([IO.Path]::IsPathRooted($Path)) {
        $Path
    }
    else {
        Join-Path $script:ResolvedRepositoryRoot $Path
    }

    Assert-Contract (Test-Path -LiteralPath $candidate -PathType Leaf) "Required file '$candidate' was not found."
    return (Resolve-Path -LiteralPath $candidate).Path
}

function Get-XmlPropertyValue {
    param(
        [xml]$Document,
        [string]$PropertyName
    )

    $node = $Document.SelectSingleNode("//*[local-name()='$PropertyName']")
    if ($null -eq $node) {
        return $null
    }

    return $node.InnerText.Trim()
}

$script:ResolvedRepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$resolvedChangelogPath = Resolve-RepositoryPath $ChangelogPath

$expectedCommitText = $ExpectedCommit.Trim()
Assert-Contract ($expectedCommitText -match '^[0-9a-fA-F]{40,64}$') "Expected commit must be a full hexadecimal Git object id."

$actualCommit = (& git -C $script:ResolvedRepositoryRoot rev-parse HEAD 2>&1 | Out-String).Trim()
$gitExitCode = $LASTEXITCODE
Assert-Contract ($gitExitCode -eq 0 -and $actualCommit -match '^[0-9a-fA-F]{40,64}$') "Could not resolve the checked-out commit."
Assert-Contract ($actualCommit.Equals($expectedCommitText, [StringComparison]::OrdinalIgnoreCase)) "Checked-out commit '$actualCommit' does not match expected commit '$expectedCommitText'."

$relativeChangelogPath = [IO.Path]::GetRelativePath($script:ResolvedRepositoryRoot, $resolvedChangelogPath)
if (-not [IO.Path]::IsPathRooted($relativeChangelogPath) -and
    $relativeChangelogPath -ne ".." -and
    -not $relativeChangelogPath.StartsWith("..$([IO.Path]::DirectorySeparatorChar)") -and
    -not $relativeChangelogPath.StartsWith("..$([IO.Path]::AltDirectorySeparatorChar)")) {
    $relativeChangelogPath = $relativeChangelogPath.Replace([IO.Path]::DirectorySeparatorChar, '/').Replace([IO.Path]::AltDirectorySeparatorChar, '/')
    $trackedPath = (& git -C $script:ResolvedRepositoryRoot ls-files --error-unmatch -- $relativeChangelogPath 2>&1 | Out-String).Trim()
    $gitExitCode = $LASTEXITCODE
    Assert-Contract ($gitExitCode -eq 0 -and $trackedPath -eq $relativeChangelogPath) "Changelog '$relativeChangelogPath' must be tracked in the checked-out commit."

    & git -C $script:ResolvedRepositoryRoot diff --quiet HEAD -- $relativeChangelogPath
    $gitExitCode = $LASTEXITCODE
    Assert-Contract ($gitExitCode -eq 0) "Changelog '$relativeChangelogPath' differs from the supplied commit '$expectedCommitText'."
}

$changelog = [IO.File]::ReadAllText($resolvedChangelogPath)
$headingMatches = [Text.RegularExpressions.Regex]::Matches(
    $changelog,
    '(?m)^(?<level>#{1,6})[ \t]+(?<title>[^\r\n]+?)[ \t]*\r?$')
$releaseHeadingPattern = '^[ ]*\[?(?<version>\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?)\]?(?:[ \t]+-[ \t]+(?<suffix>.*?))?[ \t]*$'
$headingStack = [Collections.Generic.List[object]]::new()
$headings = @(
    foreach ($headingMatch in $headingMatches) {
        $title = $headingMatch.Groups["title"].Value.Trim()
        $releaseMatch = [Text.RegularExpressions.Regex]::Match($title, $releaseHeadingPattern)
        $heading = [pscustomobject]@{
            Index = $headingMatch.Index
            Level = $headingMatch.Groups["level"].Value.Length
            Title = $title
            ReleaseVersion = if ($releaseMatch.Success) { $releaseMatch.Groups["version"].Value } else { $null }
            ReleaseSuffix = if ($releaseMatch.Success) { $releaseMatch.Groups["suffix"].Value.Trim() } else { $null }
            Ancestors = @()
        }

        while ($headingStack.Count -gt 0 -and $headingStack[$headingStack.Count - 1].Level -ge $heading.Level) {
            $headingStack.RemoveAt($headingStack.Count - 1)
        }

        $heading.Ancestors = @($headingStack.ToArray())
        $headingStack.Add($heading)
        $heading
    }
)

$targetHeadings = @($headings | Where-Object { $_.ReleaseVersion -ceq $ExpectedVersion })
Assert-Contract ($targetHeadings.Count -eq 1) "Expected exactly one release heading for version '$ExpectedVersion'."
$targetHeading = $targetHeadings[0]

$preReleaseStatusWords = 'planned|unreleased|tbd|draft|upcoming|pending|forthcoming|pre-?release'
$preReleaseStatusPhrases = 'not\s+yet\s+(?:published|released)'
$preReleaseStatusTokens = "(?:$preReleaseStatusWords|not\s+(?:published|released)|to\s+be\s+(?:published|released)|not\s+ready|work\s+in\s+progress|coming\s+soon)"
$preReleaseMarkerPattern = "(?i)(?<![A-Za-z])$preReleaseStatusTokens(?![A-Za-z])|(?i)\b$preReleaseStatusPhrases\b"
$ancestorWithMarker = @($targetHeading.Ancestors |
    Where-Object { $_.Title -match $preReleaseMarkerPattern } |
    Select-Object -First 1)
Assert-Contract ($ancestorWithMarker.Count -eq 0) "Release version '$ExpectedVersion' is nested inside a pre-release section."

Assert-Contract ($targetHeading.Title -notmatch $preReleaseMarkerPattern) "Release heading for '$ExpectedVersion' is still marked as planned or unpublished."

$releaseStatusContextPattern = '(?i)\b(?:release|released|publication|published|publish|package|tag|notes|entry|ship(?:ped|ping)?|launch(?:ed|ing)?)\b'
function Test-PreReleaseBodyMarker {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SectionText,

        [Parameter(Mandatory = $true)]
        [string]$TokenPattern,

        [Parameter(Mandatory = $true)]
        [string]$PhrasePattern,

        [Parameter(Mandatory = $true)]
        [string]$ContextPattern
    )

    $undecoratedSection = [Text.RegularExpressions.Regex]::Replace(
        $SectionText,
        '(?m)^[ \t]*(?:#{1,6}[ \t]*|>[ \t]*|[-*+][ \t]+|\d+[.)][ \t]+)+',
        '')
    $normalizedSection = [Text.RegularExpressions.Regex]::Replace($undecoratedSection, '\s+', ' ').Trim()
    if ([Text.RegularExpressions.Regex]::IsMatch($normalizedSection, $PhrasePattern)) {
        return $true
    }

    foreach ($sentence in ($normalizedSection -split '[.!?;:]+')) {
        foreach ($tokenMatch in [Text.RegularExpressions.Regex]::Matches($sentence, $TokenPattern)) {
            $beforeMarker = $sentence.Substring(0, $tokenMatch.Index).Trim()
            $afterMarker = $sentence.Substring($tokenMatch.Index + $tokenMatch.Length).Trim()
            $beforeWords = @($beforeMarker -split '\s+' | Where-Object { $_ } | Select-Object -Last 4)
            $afterWords = @($afterMarker -split '\s+' | Where-Object { $_ } | Select-Object -First 4)
            $contextWindow = (@($beforeWords) + @($afterWords)) -join ' '
            if ($contextWindow -match $ContextPattern) {
                return $true
            }
        }
    }

    foreach ($rawLine in ($SectionText -split "\r?\n")) {
        $statusLine = $rawLine.Trim()
        $statusLine = [Text.RegularExpressions.Regex]::Replace($statusLine, '^(?:#{1,6}[ \t]*|>[ \t]*|[-*+][ \t]+|\d+[.)][ \t]+)+', '')
        $statusLine = $statusLine.Trim('*', '_', '`', ' ', "`t")
        $statusLine = $statusLine.TrimEnd('.', '!', ';', ':', '-', '*', '_', '`', ' ', "`t")
        if ([string]::IsNullOrWhiteSpace($statusLine)) {
            continue
        }

        if ([Text.RegularExpressions.Regex]::IsMatch($statusLine, "^$TokenPattern$")) {
            return $true
        }

        if ([Text.RegularExpressions.Regex]::IsMatch($statusLine, "^(?<label>[^:]{0,60}):[ \t]*$TokenPattern$")) {
            return $true
        }
    }

    return $false
}

$nextSectionHeading = @($headings |
    Where-Object { $_.Index -gt $targetHeading.Index -and $_.Level -le $targetHeading.Level } |
    Sort-Object Index |
    Select-Object -First 1)
$sectionEndIndex = if ($nextSectionHeading.Count -eq 1) {
    $nextSectionHeading[0].Index
}
else {
    $changelog.Length
}
$targetSection = $changelog.Substring($targetHeading.Index, $sectionEndIndex - $targetHeading.Index)
Assert-Contract (-not (Test-PreReleaseBodyMarker `
        -SectionText $targetSection `
        -TokenPattern "(?i)(?<![A-Za-z])$preReleaseStatusTokens(?![A-Za-z])" `
        -PhrasePattern "(?i)\b$preReleaseStatusPhrases\b" `
        -ContextPattern $releaseStatusContextPattern)) "Release section for '$ExpectedVersion' is still marked as planned or unpublished."

$releaseDateText = $targetHeading.ReleaseSuffix
Assert-Contract (-not [string]::IsNullOrWhiteSpace($releaseDateText)) "Release date for '$ExpectedVersion' is missing."
Assert-Contract ($releaseDateText -match '^\d{4}-\d{2}-\d{2}$') "Release date '$releaseDateText' for '$ExpectedVersion' is invalid or a placeholder."
$releaseDate = [DateTime]::MinValue
$parsedDate = [DateTime]::TryParseExact(
    $releaseDateText,
    "yyyy-MM-dd",
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::None,
    [ref]$releaseDate)
Assert-Contract $parsedDate "Release date '$releaseDateText' for '$ExpectedVersion' is invalid."
Assert-Contract ($releaseDate.Date -le [DateTime]::UtcNow.Date) "Release date '$releaseDateText' for '$ExpectedVersion' is later than the current UTC date."

$expectedPackageVersionToCheck = if ([string]::IsNullOrWhiteSpace($ExpectedPackageVersion)) {
    $ExpectedVersion
}
else {
    $ExpectedPackageVersion
}
Assert-VersionMatches $expectedPackageVersionToCheck $ExpectedVersion "Expected package version"

$buildPropsPath = Join-Path $script:ResolvedRepositoryRoot "Directory.Build.props"
if (Test-Path -LiteralPath $buildPropsPath -PathType Leaf) {
    try {
        [xml]$buildProps = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $buildPropsPath).Path)
    }
    catch {
        Fail-Contract "Directory.Build.props is not valid XML: $($_.Exception.Message)"
    }

    $declaredVersion = Get-XmlPropertyValue $buildProps "Version"
    $declaredPackageVersion = Get-XmlPropertyValue $buildProps "PackageVersion"
    if ([string]::IsNullOrWhiteSpace($declaredPackageVersion) -or $declaredPackageVersion -eq "`$(Version)") {
        $declaredPackageVersion = $declaredVersion
    }

    Assert-VersionMatches $declaredVersion $ExpectedVersion "Directory.Build.props Version"
    Assert-VersionMatches $declaredPackageVersion $expectedPackageVersionToCheck "Directory.Build.props PackageVersion"
}
elseif (-not [string]::IsNullOrWhiteSpace($ExpectedPackageVersion)) {
    Fail-Contract "Expected package version was supplied, but Directory.Build.props was not found."
}

$centralVersionsPath = Join-Path $script:ResolvedRepositoryRoot "Directory.Packages.props"
if (Test-Path -LiteralPath $centralVersionsPath -PathType Leaf) {
    try {
        [xml]$centralVersions = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $centralVersionsPath).Path)
    }
    catch {
        Fail-Contract "Directory.Packages.props is not valid XML: $($_.Exception.Message)"
    }

    foreach ($packageVersionNode in @($centralVersions.SelectNodes("//*[local-name()='PackageVersion']"))) {
        $packageName = $packageVersionNode.GetAttribute("Include")
        if ($packageName -like "KeelMatrix.*" -and $packageName -ne "KeelMatrix.FixtureVault") {
            $dependencyVersion = $packageVersionNode.GetAttribute("Version")
            Assert-VersionMatches $dependencyVersion $ExpectedVersion "Dependency '$packageName'"
        }
    }
}

$readmePath = Join-Path $script:ResolvedRepositoryRoot "README.md"
if (Test-Path -LiteralPath $readmePath -PathType Leaf) {
    $readme = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $readmePath).Path)
    $installVersionPatterns = @(
        '(?im)\bKeelMatrix\.FixtureVault\b[^\r\n]*?--version(?:\s+|=)(?<version>[^\s`"''<>]+)',
        '(?im)\bKeelMatrix\.FixtureVault\b[^\r\n]*(?:(?:\\|`)[ \t]*)?\r?\n[ \t]*--version(?:\s+|=)(?<version>[^\s`"''<>]+)'
    )
    foreach ($installVersionPattern in $installVersionPatterns) {
        foreach ($match in [Text.RegularExpressions.Regex]::Matches($readme, $installVersionPattern)) {
            Assert-VersionMatches $match.Groups["version"].Value $ExpectedVersion "README install example"
        }
    }

    $artifactVersionMatches = [Text.RegularExpressions.Regex]::Matches(
        $readme,
        '(?i)\bKeelMatrix\.FixtureVault\.(?<version>\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?)\.(?:nupkg|snupkg)\b')
    foreach ($match in $artifactVersionMatches) {
        Assert-VersionMatches $match.Groups["version"].Value $ExpectedVersion "README package example"
    }
}

Write-Host "Changelog/version contract passed for $ExpectedVersion on commit $actualCommit."
