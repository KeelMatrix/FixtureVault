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
    if ($normalized.Length -ge 2) {
        $openingQuote = $normalized[0]
        $closingQuote = $normalized[$normalized.Length - 1]
        if ($openingQuote -in @([char]34, [char]39, [char]96) -and $closingQuote -eq $openingQuote) {
            $normalized = $normalized.Substring(1, $normalized.Length - 2).Trim()
        }
    }

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

function Normalize-ChangelogText {
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Text
    )

    if ($null -eq $Text) {
        return ""
    }

    $normalized = $Text.ToLowerInvariant()
    $normalized = [Text.RegularExpressions.Regex]::Replace(
        $normalized,
        '(?m)^[\p{Zs}\t]*(?:(?:#{1,6}|[-*+])[\p{Zs}\t]+|\d+[.)][\p{Zs}\t]+|>[\p{Zs}\t]*)+',
        '')
    $normalized = [Text.RegularExpressions.Regex]::Replace(
        $normalized,
        '(?<![\p{L}\p{N}])[*_~`]+|[*_~`]+(?![\p{L}\p{N}])',
        '')
    $normalized = [Text.RegularExpressions.Regex]::Replace($normalized, '\s+', ' ').Trim()
    return $normalized
}

function Get-NormalizedTokens {
    param([AllowEmptyString()][string]$NormalizedText)

    return @(
        [Text.RegularExpressions.Regex]::Matches($NormalizedText, '[\p{L}\p{N}]+') |
            ForEach-Object { $_.Value }
    )
}

function Test-TokenSequence {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Tokens,

        [Parameter(Mandatory = $true)]
        [string[]]$MarkerTokens,

        [int]$StartIndex
    )

    if ($StartIndex + $MarkerTokens.Count -gt $Tokens.Count) {
        return $false
    }

    for ($offset = 0; $offset -lt $MarkerTokens.Count; $offset++) {
        if ($Tokens[$StartIndex + $offset] -cne $MarkerTokens[$offset]) {
            return $false
        }
    }

    return $true
}

function Test-AsciiDigits {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ($Value.Length -eq 0) {
        return $false
    }

    for ($index = 0; $index -lt $Value.Length; $index++) {
        $codePoint = [int][char]$Value[$index]
        if ($codePoint -lt 48 -or $codePoint -gt 57) {
            return $false
        }
    }

    return $true
}

function Test-SemanticIdentifiers {
    param([Parameter(Mandatory = $true)][string]$Value)

    foreach ($identifier in @($Value.Split('.'))) {
        if ($identifier.Length -eq 0) {
            return $false
        }

        for ($index = 0; $index -lt $identifier.Length; $index++) {
            $codePoint = [int][char]$identifier[$index]
            $isDigit = $codePoint -ge 48 -and $codePoint -le 57
            $isUpper = $codePoint -ge 65 -and $codePoint -le 90
            $isLower = $codePoint -ge 97 -and $codePoint -le 122
            if (-not ($isDigit -or $isUpper -or $isLower -or $codePoint -eq 45)) {
                return $false
            }
        }
    }

    return $true
}

function ConvertTo-SemanticVersion {
    param([Parameter(Mandatory = $true)][string]$Value)

    $trimmedValue = $Value.Trim()
    $buildSeparator = $trimmedValue.IndexOf('+')
    $withoutBuild = $trimmedValue
    if ($buildSeparator -ge 0) {
        if ($trimmedValue.IndexOf('+', $buildSeparator + 1) -ge 0 -or $buildSeparator -eq $trimmedValue.Length - 1) {
            return $null
        }

        $withoutBuild = $trimmedValue.Substring(0, $buildSeparator)
        if (-not (Test-SemanticIdentifiers $trimmedValue.Substring($buildSeparator + 1))) {
            return $null
        }
    }

    $preReleaseSeparator = $withoutBuild.IndexOf('-')
    $coreText = if ($preReleaseSeparator -ge 0) {
        $withoutBuild.Substring(0, $preReleaseSeparator)
    }
    else {
        $withoutBuild
    }
    $coreParts = @($coreText.Split('.'))
    if ($coreParts.Count -ne 3 -or @($coreParts | Where-Object { -not (Test-AsciiDigits $_) }).Count -gt 0) {
        return $null
    }

    $preRelease = @()
    if ($preReleaseSeparator -ge 0) {
        $preReleaseText = $withoutBuild.Substring($preReleaseSeparator + 1)
        if (-not (Test-SemanticIdentifiers $preReleaseText)) {
            return $null
        }

        $preRelease = @($preReleaseText.Split('.'))
    }

    return [pscustomobject]@{
        Major = [Numerics.BigInteger]::Parse($coreParts[0], [Globalization.CultureInfo]::InvariantCulture)
        Minor = [Numerics.BigInteger]::Parse($coreParts[1], [Globalization.CultureInfo]::InvariantCulture)
        Patch = [Numerics.BigInteger]::Parse($coreParts[2], [Globalization.CultureInfo]::InvariantCulture)
        PreRelease = $preRelease
    }
}

function Compare-SemanticVersions {
    param(
        [Parameter(Mandatory = $true)][object]$Left,
        [Parameter(Mandatory = $true)][object]$Right
    )

    foreach ($component in @("Major", "Minor", "Patch")) {
        $comparison = $Left.$component.CompareTo($Right.$component)
        if ($comparison -ne 0) {
            return $comparison
        }
    }

    $leftPreRelease = @($Left.PreRelease)
    $rightPreRelease = @($Right.PreRelease)
    if ($leftPreRelease.Count -eq 0 -and $rightPreRelease.Count -eq 0) {
        return 0
    }
    if ($leftPreRelease.Count -eq 0) {
        return 1
    }
    if ($rightPreRelease.Count -eq 0) {
        return -1
    }

    $identifierCount = [Math]::Min($leftPreRelease.Count, $rightPreRelease.Count)
    for ($index = 0; $index -lt $identifierCount; $index++) {
        $leftIdentifier = $leftPreRelease[$index]
        $rightIdentifier = $rightPreRelease[$index]
        if ($leftIdentifier -ceq $rightIdentifier) {
            continue
        }

        $leftIsNumeric = Test-AsciiDigits $leftIdentifier
        $rightIsNumeric = Test-AsciiDigits $rightIdentifier
        if ($leftIsNumeric -and $rightIsNumeric) {
            return ([Numerics.BigInteger]::Parse($leftIdentifier, [Globalization.CultureInfo]::InvariantCulture)).CompareTo(
                [Numerics.BigInteger]::Parse($rightIdentifier, [Globalization.CultureInfo]::InvariantCulture))
        }
        if ($leftIsNumeric) {
            return -1
        }
        if ($rightIsNumeric) {
            return 1
        }

        return [string]::Compare($leftIdentifier, $rightIdentifier, [StringComparison]::Ordinal)
    }

    return $leftPreRelease.Count.CompareTo($rightPreRelease.Count)
}

function Find-FirstReleaseBannedWording {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Text
    )

    $tokens = @(Get-NormalizedTokens (Normalize-ChangelogText $Text))
    $bannedWordings = @(
        'now', 'no longer', 'previously', 'formerly', 'used to',
        'fixed', 'fixes', 'corrected', 'resolved', 'addressed',
        'this removes', 'this fixes', 'changed from'
    ) | ForEach-Object {
        [pscustomobject]@{
            Phrase = $_
            Tokens = @(Get-NormalizedTokens (Normalize-ChangelogText $_))
        }
    }

    # The wording list is fixed and small; each token position is visited once
    # for a bounded number of normalized token sequences.
    for ($index = 0; $index -lt $tokens.Count; $index++) {
        foreach ($bannedWording in $bannedWordings) {
            if ($tokens[$index] -cne $bannedWording.Tokens[0]) {
                continue
            }

            if (Test-TokenSequence -Tokens $tokens -MarkerTokens $bannedWording.Tokens -StartIndex $index) {
                return $bannedWording.Phrase
            }
        }
    }

    return $null
}

function Test-PreReleaseMarkerScan {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [object[]]$MarkerDefinitions
    )

    $normalizedText = Normalize-ChangelogText $Text
    $tokens = @(Get-NormalizedTokens $normalizedText)
    if ($tokens.Count -eq 0) {
        return $false
    }

    $releaseContextTokens = @(
        'release', 'released', 'publication', 'published', 'publish',
        'package', 'tag', 'note', 'notes', 'entry', 'ship', 'shipped',
        'shipping', 'launch', 'launched', 'launching', 'status', 'state'
    )

    foreach ($marker in $MarkerDefinitions) {
        $markerTokens = @(Get-NormalizedTokens (Normalize-ChangelogText $marker.Phrase))
        if ($markerTokens.Count -eq 0) {
            continue
        }

        for ($index = 0; $index -le $tokens.Count - $markerTokens.Count; $index++) {
            if (-not (Test-TokenSequence -Tokens $tokens -MarkerTokens $markerTokens -StartIndex $index)) {
                continue
            }

            $windowStart = [Math]::Max(0, $index - 4)
            $windowEnd = [Math]::Min($tokens.Count - 1, $index + $markerTokens.Count + 3)
            $contextWindow = @($tokens[$windowStart..$windowEnd])
            $hasReleaseContext = @($contextWindow | Where-Object {
                    $releaseContextTokens -contains $_ -and
                    $_ -notin $markerTokens
                }).Count -gt 0

            # "Draft API type" is ordinary changelog prose, not a release state.
            # Keep this exclusion narrow: a nearby release/status word still wins.
            $isDraftApiProse = $marker.Phrase -eq 'draft' -and
                $index + $markerTokens.Count -lt $tokens.Count -and
                $tokens[$index + $markerTokens.Count] -eq 'api' -and
                -not $hasReleaseContext
            if ($isDraftApiProse) {
                continue
            }

            if (-not $marker.RequiresReleaseContext) {
                return $true
            }

            $isStandaloneStatus = $index + $markerTokens.Count -eq $tokens.Count
            if ($hasReleaseContext -or $isStandaloneStatus) {
                return $true
            }
        }
    }

    return $false
}

# These are deliberately narrow, normalized token sequences for release states that
# must not pass the publication gate. Contextual legacy entries preserve useful prose
# such as "work in progress files"; the narrow Draft/API boundary below does the
# same for "Draft API type" while explicit release-status wording still fails.
$preReleaseMarkerDefinitions = @(
    [pscustomobject]@{ Phrase = 'unreleased'; RequiresReleaseContext = $false },
    [pscustomobject]@{ Phrase = 'planned'; RequiresReleaseContext = $false },
    [pscustomobject]@{ Phrase = 'not yet published'; RequiresReleaseContext = $false },
    [pscustomobject]@{ Phrase = 'not yet released'; RequiresReleaseContext = $false },
    [pscustomobject]@{ Phrase = 'tbd'; RequiresReleaseContext = $false },
    [pscustomobject]@{ Phrase = 'draft'; RequiresReleaseContext = $false },
    [pscustomobject]@{ Phrase = 'pending'; RequiresReleaseContext = $false },
    [pscustomobject]@{ Phrase = 'not ready'; RequiresReleaseContext = $true },
    [pscustomobject]@{ Phrase = 'not published'; RequiresReleaseContext = $true },
    [pscustomobject]@{ Phrase = 'to be published'; RequiresReleaseContext = $true },
    [pscustomobject]@{ Phrase = 'to be released'; RequiresReleaseContext = $true },
    [pscustomobject]@{ Phrase = 'upcoming'; RequiresReleaseContext = $true },
    [pscustomobject]@{ Phrase = 'forthcoming'; RequiresReleaseContext = $true },
    [pscustomobject]@{ Phrase = 'pre-release'; RequiresReleaseContext = $true },
    [pscustomobject]@{ Phrase = 'work in progress'; RequiresReleaseContext = $true },
    [pscustomobject]@{ Phrase = 'coming soon'; RequiresReleaseContext = $true }
)

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
Assert-Contract (-not (Test-PreReleaseMarkerScan `
        -Text $targetSection `
        -MarkerDefinitions $preReleaseMarkerDefinitions)) "Release section for '$ExpectedVersion' is still marked as planned or unpublished."

$targetSemanticVersion = ConvertTo-SemanticVersion $targetHeading.ReleaseVersion
Assert-Contract ($null -ne $targetSemanticVersion) "Release heading version '$($targetHeading.ReleaseVersion)' is not a valid semantic version."
$hasLowerRelease = $false
foreach ($releaseHeading in @($headings | Where-Object { $null -ne $_.ReleaseVersion })) {
    $releaseSemanticVersion = ConvertTo-SemanticVersion $releaseHeading.ReleaseVersion
    if ($null -ne $releaseSemanticVersion -and
        (Compare-SemanticVersions -Left $releaseSemanticVersion -Right $targetSemanticVersion) -lt 0) {
        $hasLowerRelease = $true
        break
    }
}

if (-not $hasLowerRelease) {
    $targetSectionHeadings = @($headings | Where-Object {
            $_.Index -gt $targetHeading.Index -and $_.Index -lt $sectionEndIndex
        })
    $addedHeadingCount = 0
    foreach ($sectionHeading in $targetSectionHeadings) {
        $normalizedHeadingTitle = Normalize-ChangelogText $sectionHeading.Title
        Assert-Contract ($normalizedHeadingTitle -ceq "added") "First release '$ExpectedVersion' contains non-Added heading '$($sectionHeading.Title)'."
        $addedHeadingCount++
    }

    Assert-Contract ($addedHeadingCount -gt 0) "First release '$ExpectedVersion' must contain a non-empty Added section."

    $bannedWording = Find-FirstReleaseBannedWording $targetSection
    Assert-Contract ($null -eq $bannedWording) "First release '$ExpectedVersion' contains unpublished transition/remediation wording '$bannedWording'."
}

function Get-HeadingDirectScope {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Heading,

        [Parameter(Mandatory = $true)]
        [object[]]$AllHeadings,

        [Parameter(Mandatory = $true)]
        [string]$Document
    )

    $nextHeading = @($AllHeadings |
        Where-Object { $_.Index -gt $Heading.Index } |
        Sort-Object Index |
        Select-Object -First 1)
    $endIndex = if ($nextHeading.Count -eq 1) {
        $nextHeading[0].Index
    }
    else {
        $Document.Length
    }

    return $Document.Substring($Heading.Index, $endIndex - $Heading.Index)
}

foreach ($ancestor in @($targetHeading.Ancestors)) {
    $ancestorScope = Get-HeadingDirectScope -Heading $ancestor -AllHeadings $headings -Document $changelog
    Assert-Contract (-not (Test-PreReleaseMarkerScan `
            -Text $ancestorScope `
            -MarkerDefinitions $preReleaseMarkerDefinitions)) "Release version '$ExpectedVersion' is nested inside a pre-release section."
}

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
        '(?im)\bKeelMatrix\.FixtureVault\b[^\r\n]*?--version(?:\s+|=)(?<version>"[^"\r\n]*"|''[^''\r\n]*''|`[^`\r\n]*`|[^\s"''`<>]+)',
        '(?im)\bKeelMatrix\.FixtureVault\b[^\r\n]*(?:(?:\\|`)[ \t]*)?\r?\n[ \t]*--version(?:\s+|=)(?<version>"[^"\r\n]*"|''[^''\r\n]*''|`[^`\r\n]*`|[^\s"''`<>]+)'
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
