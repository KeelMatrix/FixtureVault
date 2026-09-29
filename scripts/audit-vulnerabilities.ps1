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

function Normalize-ProjectPath {
    param(
        [string]$Path,
        [string]$BaseDirectory
    )

    try {
        $candidate = if ([IO.Path]::IsPathRooted($Path)) {
            $Path
        }
        else {
            Join-Path $BaseDirectory $Path
        }

        return [IO.Path]::GetFullPath($candidate).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    }
    catch {
        Fail-Audit "a project path could not be normalized."
    }
}

function Get-ExpectedProjectPaths {
    param([string]$Path)

    $solutionFullPath = [IO.Path]::GetFullPath($Path)
    if (-not [IO.File]::Exists($solutionFullPath)) {
        Fail-Audit "the solution file '$Path' does not exist."
    }

    $solutionDirectory = [IO.Path]::GetDirectoryName($solutionFullPath)
    if ([string]::IsNullOrWhiteSpace($solutionDirectory)) {
        Fail-Audit "the solution directory could not be determined."
    }

    $expected = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    if ([OperatingSystem]::IsWindows()) {
        $expected = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    }

    $projectPattern = '^\s*Project\([^)]*\)\s*=\s*"[^"]+"\s*,\s*"([^"]+\.csproj)"\s*,'
    foreach ($line in [IO.File]::ReadLines($solutionFullPath)) {
        $match = [regex]::Match($line, $projectPattern)
        if ($match.Success) {
            $projectPath = Normalize-ProjectPath $match.Groups[1].Value $solutionDirectory
            if (-not $expected.TryAdd($projectPath, $projectPath)) {
                Fail-Audit "the solution lists project '$projectPath' more than once."
            }
        }
    }

    if ($expected.Count -eq 0) {
        Fail-Audit "the solution contains no project entries."
    }

    return $expected
}

function Get-JsonProperty {
    param(
        [object]$Object,
        [string]$Name,
        [bool]$Required = $true
    )

    $property = @($Object.PSObject.Properties | Where-Object { $_.Name -eq $Name })
    if ($property.Count -eq 0) {
        if ($Required) {
            Fail-Audit "structured output is missing '$Name'."
        }

        return $null
    }

    return $property[0].Value
}

function Assert-AllowedProperties {
    param(
        [object]$Object,
        [string[]]$Allowed,
        [string]$Context
    )

    foreach ($property in $Object.PSObject.Properties) {
        if ($Allowed -notcontains $property.Name) {
            Fail-Audit "structured output contains unrecognized property '$($property.Name)' in $Context."
        }
    }
}

function Get-JsonArray {
    param(
        [object]$Value,
        [string]$Context
    )

    if ($null -eq $Value -or
        $Value -is [string] -or
        $Value -isnot [Collections.IEnumerable]) {
        Fail-Audit "structured output property '$Context' is not an array."
    }

    return @($Value)
}

$expectedProjects = Get-ExpectedProjectPaths $SolutionPath
$solutionDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($SolutionPath))
$arguments = @(
    "list",
    $SolutionPath,
    "package",
    "--vulnerable",
    "--include-transitive",
    "--format",
    "json",
    "--output-version",
    "1"
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

try {
    $audit = $auditOutput | ConvertFrom-Json -Depth 100
}
catch {
    Fail-Audit "the advisory command returned malformed structured output."
}

try {
    $jsonDocument = [Text.Json.JsonDocument]::Parse($auditOutput)
}
catch {
    Fail-Audit "the advisory command returned malformed structured output."
}

if ($null -eq $audit -or $audit -is [string] -or
    $jsonDocument.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
    Fail-Audit "the advisory command returned a non-object structured result."
}

Assert-AllowedProperties $audit @("version", "parameters", "sources", "projects", "problems") "the audit result"
$version = Get-JsonProperty $audit "version"
if ($version -ne 1) {
    Fail-Audit "the advisory result format version '$version' is not supported."
}

$parameters = Get-JsonProperty $audit "parameters"
if ($parameters -isnot [string] -or
    $parameters -notmatch '(?<!\S)--vulnerable(?!\S)' -or
    $parameters -notmatch '(?<!\S)--include-transitive(?!\S)') {
    Fail-Audit "the advisory result does not prove direct and transitive vulnerability coverage."
}

$sourcesElement = $jsonDocument.RootElement.GetProperty("sources")
if ($sourcesElement.ValueKind -ne [Text.Json.JsonValueKind]::Array) {
    Fail-Audit "structured output property 'sources' is not an array."
}
$sourcesValue = Get-JsonProperty $audit "sources"
$sources = @(if ($sourcesValue -is [string]) {
    @($sourcesValue)
}
else {
    Get-JsonArray $sourcesValue "sources"
})
if ($sources.Count -eq 0 -or @($sources | Where-Object { $_ -isnot [string] -or [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
    Fail-Audit "the advisory result contains no usable package sources."
}

$problemsProperty = Get-JsonProperty $audit "problems" $false
if ($null -ne $problemsProperty) {
    $problems = @(Get-JsonArray $problemsProperty "problems")
    if ($problems.Count -gt 0) {
        foreach ($problem in $problems) {
            if ($null -eq $problem -or $problem -is [string]) {
                Fail-Audit "the advisory result contains a malformed failure entry."
            }

            Assert-AllowedProperties $problem @("project", "framework", "level", "text") "a failure entry"
            $level = Get-JsonProperty $problem "level"
            $text = Get-JsonProperty $problem "text"
            if ($level -isnot [string] -or $text -isnot [string] -or [string]::IsNullOrWhiteSpace($text)) {
                Fail-Audit "the advisory result contains a malformed failure entry."
            }
        }

        Fail-Audit "the advisory result reported unavailable or failed coverage."
    }
}

$projectsElement = $jsonDocument.RootElement.GetProperty("projects")
if ($projectsElement.ValueKind -ne [Text.Json.JsonValueKind]::Array) {
    Fail-Audit "structured output property 'projects' is not an array."
}
$projects = @(Get-JsonArray (Get-JsonProperty $audit "projects") "projects")
if ($projects.Count -ne $expectedProjects.Count) {
    Fail-Audit "the advisory result covered $($projects.Count) project(s), but the solution requires $($expectedProjects.Count)."
}

$seenProjects = [Collections.Generic.HashSet[string]]::new($expectedProjects.Comparer)
foreach ($project in $projects) {
    if ($null -eq $project -or $project -is [string]) {
        Fail-Audit "the advisory result contains a malformed project entry."
    }

    Assert-AllowedProperties $project @("path", "frameworks") "a project entry"
    $projectPathValue = Get-JsonProperty $project "path"
    if ($projectPathValue -isnot [string] -or [string]::IsNullOrWhiteSpace($projectPathValue)) {
        Fail-Audit "the advisory result contains a project without a path."
    }

    $projectPath = Normalize-ProjectPath $projectPathValue $solutionDirectory
    if (-not $expectedProjects.ContainsKey($projectPath)) {
        Fail-Audit "the advisory result contains unrelated project '$projectPathValue'."
    }

    if (-not $seenProjects.Add($projectPath)) {
        Fail-Audit "the advisory result contains duplicate project '$projectPathValue'."
    }

    $frameworksProperty = Get-JsonProperty $project "frameworks" $false
    if ($null -eq $frameworksProperty) {
        continue
    }

    $frameworks = @(Get-JsonArray $frameworksProperty "frameworks for '$projectPathValue'")
    $seenFrameworks = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($framework in $frameworks) {
        if ($null -eq $framework -or $framework -is [string]) {
            Fail-Audit "the advisory result contains a malformed framework entry."
        }

        Assert-AllowedProperties $framework @("framework", "topLevelPackages", "transitivePackages") "a framework entry"
        $frameworkName = Get-JsonProperty $framework "framework"
        if ($frameworkName -isnot [string] -or [string]::IsNullOrWhiteSpace($frameworkName) -or
            -not $seenFrameworks.Add($frameworkName)) {
            Fail-Audit "the advisory result contains a missing or duplicate framework."
        }

        foreach ($packagePropertyName in @("topLevelPackages", "transitivePackages")) {
            $packageProperty = Get-JsonProperty $framework $packagePropertyName $false
            if ($null -eq $packageProperty) {
                continue
            }

            $packages = @(Get-JsonArray $packageProperty "$packagePropertyName for '$projectPathValue'/$frameworkName")
            if ($packages.Count -gt 0) {
                Fail-Audit "the advisory result reported one or more vulnerable packages."
            }
        }
    }
}

foreach ($expectedProject in $expectedProjects.Keys) {
    if (-not $seenProjects.Contains($expectedProject)) {
        Fail-Audit "the advisory result omitted expected project '$expectedProject'."
    }
}

Write-Host "Dependency vulnerability audit passed: $($seenProjects.Count) solution project result(s) have complete direct and transitive structured coverage."
