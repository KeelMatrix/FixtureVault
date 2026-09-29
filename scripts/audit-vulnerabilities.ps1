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

function Get-ExpectedProjectFrameworks {
    param(
        [Collections.Generic.Dictionary[string, string]]$Projects
    )

    $frameworksByProject = [Collections.Generic.Dictionary[string, string[]]]::new($Projects.Comparer)
    foreach ($projectPath in $Projects.Keys) {
        try {
            $projectDocument = [xml]::new()
            $projectDocument.Load($projectPath)
        }
        catch {
            Fail-Audit "project '$projectPath' could not be parsed to determine its target frameworks."
        }

        $frameworks = [Collections.Generic.List[string]]::new()
        $frameworkNodes = @($projectDocument.SelectNodes("//*[local-name()='TargetFramework' or local-name()='TargetFrameworks']"))
        foreach ($frameworkNode in $frameworkNodes) {
            foreach ($framework in ([string]$frameworkNode.InnerText).Split(';', [StringSplitOptions]::RemoveEmptyEntries)) {
                $normalizedFramework = $framework.Trim()
                if ([string]::IsNullOrWhiteSpace($normalizedFramework) -or $normalizedFramework.Contains('$')) {
                    Fail-Audit "project '$projectPath' contains an unresolved or empty target framework."
                }

                if ($frameworks.Contains($normalizedFramework)) {
                    Fail-Audit "project '$projectPath' declares target framework '$normalizedFramework' more than once."
                }

                $frameworks.Add($normalizedFramework)
            }
        }

        if ($frameworks.Count -eq 0) {
            Fail-Audit "project '$projectPath' does not declare a target framework."
        }

        $frameworksByProject.Add($projectPath, $frameworks.ToArray())
    }

    return $frameworksByProject
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

    if ($null -eq $Value -or $Value -is [string]) {
        Fail-Audit "structured output property '$Context' is not an array."
    }

    # ConvertFrom-Json unwraps a one-element JSON array to a PSCustomObject in
    # Windows PowerShell. Preserve that one element while still rejecting scalar
    # values and strings.
    if ($Value -is [pscustomobject]) {
        return @($Value)
    }

    if ($Value -isnot [Collections.IEnumerable]) {
        Fail-Audit "structured output property '$Context' is not an array."
    }

    return @($Value)
}

function Get-RequiredJsonArrayElement {
    param(
        [Text.Json.JsonElement]$ObjectElement,
        [string]$Name,
        [string]$Context
    )

    try {
        $property = $ObjectElement.GetProperty($Name)
    }
    catch {
        Fail-Audit "structured output is missing array property '$Context'."
    }

    if ($property.ValueKind -ne [Text.Json.JsonValueKind]::Array) {
        Fail-Audit "structured output property '$Context' is not an array."
    }

    return $property
}

function Get-OptionalJsonArrayElement {
    param(
        [Text.Json.JsonElement]$ObjectElement,
        [string]$Name,
        [string]$Context
    )

    [Text.Json.JsonElement]$property = [Text.Json.JsonElement]::new()
    if (-not $ObjectElement.TryGetProperty($Name, [ref]$property)) {
        return $null
    }

    if ($property.ValueKind -ne [Text.Json.JsonValueKind]::Array) {
        Fail-Audit "structured output property '$Context' is not an array."
    }

    return $property
}

function Invoke-AuditCommand {
    param([string[]]$Arguments)

    try {
        $lines = @(& $DotnetCommand @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    catch {
        Fail-Audit "the advisory command could not be started."
    }

    $output = ($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    if ([string]::IsNullOrWhiteSpace($output)) {
        Fail-Audit "the advisory command returned no data."
    }

    try {
        $document = [Text.Json.JsonDocument]::Parse($output)
        $audit = $output | ConvertFrom-Json -Depth 100
    }
    catch {
        Fail-Audit "the advisory command returned malformed structured output."
    }

    if ($exitCode -ne 0) {
        Fail-Audit "the advisory command exited with code $exitCode."
    }

    return [pscustomobject]@{
        Output = $output
        Json = $audit
        Document = $document
    }
}

function Assert-AuditHeader {
    param(
        [object]$Audit,
        [Text.Json.JsonDocument]$Document,
        [bool]$VulnerabilityOnly
    )

    if ($null -eq $Audit -or $Audit -is [string] -or
        $Document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
        Fail-Audit "the advisory command returned a non-object structured result."
    }

    Assert-AllowedProperties $Audit @("version", "parameters", "sources", "projects", "problems") "the audit result"
    $version = Get-JsonProperty $Audit "version"
    if ($version -ne 1) {
        Fail-Audit "the advisory result format version '$version' is not supported."
    }

    $parameters = Get-JsonProperty $Audit "parameters"
    if ($parameters -isnot [string] -or
        $parameters -notmatch '(?<!\S)--include-transitive(?!\S)' -or
        ($VulnerabilityOnly -and $parameters -notmatch '(?<!\S)--vulnerable(?!\S)') -or
        (-not $VulnerabilityOnly -and $parameters -match '(?<!\S)--vulnerable(?!\S)')) {
        Fail-Audit "the advisory result does not prove the requested direct and transitive package coverage."
    }

    $sourcesProperty = Get-JsonProperty $Audit "sources" $false
    if ($null -ne $sourcesProperty) {
        $sourcesElement = Get-RequiredJsonArrayElement $Document.RootElement "sources" "sources"

        $sources = @(if ($sourcesProperty -is [string]) {
            @($sourcesProperty)
        }
        else {
            @(Get-JsonArray $sourcesProperty "sources")
        })
        if ($sources.Count -eq 0 -or @($sources | Where-Object { $_ -isnot [string] -or [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
            Fail-Audit "the advisory result contains no usable package sources."
        }
    }
}

function Assert-NoAuditProblems {
    param([object]$Audit)

    $problemsProperty = Get-JsonProperty $Audit "problems" $false
    if ($null -eq $problemsProperty) {
        return
    }

    $problems = @(Get-JsonArray $problemsProperty "problems")
    if ($problems.Count -eq 0) {
        return
    }

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

function Assert-PackageEntry {
    param(
        [object]$Package,
        [string]$Context
    )

    if ($null -eq $Package -or $Package -is [string]) {
        Fail-Audit "the advisory result contains a malformed package entry in $Context."
    }

    $id = Get-JsonProperty $Package "id"
    $resolvedVersion = Get-JsonProperty $Package "resolvedVersion"
    if ($id -isnot [string] -or [string]::IsNullOrWhiteSpace($id) -or
        $resolvedVersion -isnot [string] -or [string]::IsNullOrWhiteSpace($resolvedVersion)) {
        Fail-Audit "the advisory result contains a malformed package entry in $Context."
    }
}

function Assert-CompletePackageCoverage {
    param(
        [object]$Audit,
        [Text.Json.JsonDocument]$Document,
        [Collections.Generic.Dictionary[string, string]]$ExpectedProjects,
        [Collections.Generic.Dictionary[string, string[]]]$ExpectedFrameworks
    )

    Assert-AuditHeader $Audit $Document $false
    Assert-NoAuditProblems $Audit

    $projectsElement = $Document.RootElement.GetProperty("projects")
    if ($projectsElement.ValueKind -ne [Text.Json.JsonValueKind]::Array) {
        Fail-Audit "structured output property 'projects' is not an array."
    }

    $projects = @(Get-JsonArray (Get-JsonProperty $Audit "projects") "projects")
    $projectElements = @($projectsElement.EnumerateArray())
    if ($projects.Count -ne $ExpectedProjects.Count) {
        Fail-Audit "the complete package result covered $($projects.Count) project(s), but the solution requires $($ExpectedProjects.Count)."
    }

    $seenProjects = [Collections.Generic.HashSet[string]]::new($ExpectedProjects.Comparer)
    for ($projectIndex = 0; $projectIndex -lt $projects.Count; $projectIndex++) {
        $project = $projects[$projectIndex]
        $projectElement = $projectElements[$projectIndex]
        if ($null -eq $project -or $project -is [string]) {
            Fail-Audit "the complete package result contains a malformed project entry."
        }

        Assert-AllowedProperties $project @("path", "frameworks") "a complete package project entry"
        $projectPathValue = Get-JsonProperty $project "path"
        if ($projectPathValue -isnot [string] -or [string]::IsNullOrWhiteSpace($projectPathValue)) {
            Fail-Audit "the complete package result contains a project without a path."
        }

        $projectPath = Normalize-ProjectPath $projectPathValue ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($SolutionPath)))
        if (-not $ExpectedProjects.ContainsKey($projectPath)) {
            Fail-Audit "the complete package result contains unrelated project '$projectPathValue'."
        }

        if (-not $seenProjects.Add($projectPath)) {
            Fail-Audit "the complete package result contains duplicate project '$projectPathValue'."
        }

        $frameworksElement = Get-RequiredJsonArrayElement $projectElement "frameworks" "frameworks for '$projectPathValue'"
        $frameworks = @(Get-JsonArray (Get-JsonProperty $project "frameworks") "frameworks for '$projectPathValue'")
        $frameworkElements = @($frameworksElement.EnumerateArray())
        $expectedFrameworkNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($expectedFramework in $ExpectedFrameworks[$projectPath]) {
            $expectedFrameworkNames.Add($expectedFramework) | Out-Null
        }

        if ($frameworks.Count -ne $expectedFrameworkNames.Count) {
            Fail-Audit "the complete package result covered $($frameworks.Count) framework(s) for '$projectPathValue', but the project declares $($expectedFrameworkNames.Count)."
        }

        $seenFrameworks = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        for ($frameworkIndex = 0; $frameworkIndex -lt $frameworks.Count; $frameworkIndex++) {
            $framework = $frameworks[$frameworkIndex]
            $frameworkElement = $frameworkElements[$frameworkIndex]
            if ($null -eq $framework -or $framework -is [string]) {
                Fail-Audit "the complete package result contains a malformed framework entry."
            }

            Assert-AllowedProperties $framework @("framework", "topLevelPackages", "transitivePackages") "a complete package framework entry"
            $frameworkName = Get-JsonProperty $framework "framework"
            if ($frameworkName -isnot [string] -or [string]::IsNullOrWhiteSpace($frameworkName) -or
                -not $expectedFrameworkNames.Contains($frameworkName) -or
                -not $seenFrameworks.Add($frameworkName)) {
                Fail-Audit "the complete package result contains a missing, mismatched, or duplicate framework for '$projectPathValue'."
            }

            $topLevelContext = "topLevelPackages for '$projectPathValue'/$frameworkName"
            $transitiveContext = "transitivePackages for '$projectPathValue'/$frameworkName"
            Get-RequiredJsonArrayElement $frameworkElement "topLevelPackages" $topLevelContext | Out-Null
            Get-RequiredJsonArrayElement $frameworkElement "transitivePackages" $transitiveContext | Out-Null
            $topLevelPackages = @(Get-JsonArray (Get-JsonProperty $framework "topLevelPackages") $topLevelContext)
            $transitivePackages = @(Get-JsonArray (Get-JsonProperty $framework "transitivePackages") $transitiveContext)
            if ($topLevelPackages.Count -eq 0) {
                Fail-Audit "the complete package result contains no top-level package coverage for '$projectPathValue'/$frameworkName."
            }

            if ($transitivePackages.Count -eq 0) {
                Fail-Audit "the complete package result contains no transitive package coverage for '$projectPathValue'/$frameworkName."
            }

            foreach ($package in $topLevelPackages) {
                Assert-PackageEntry $package "topLevelPackages for '$projectPathValue'/$frameworkName"
            }

            foreach ($package in $transitivePackages) {
                Assert-PackageEntry $package "transitivePackages for '$projectPathValue'/$frameworkName"
            }
        }
    }

    foreach ($expectedProject in $ExpectedProjects.Keys) {
        if (-not $seenProjects.Contains($expectedProject)) {
            Fail-Audit "the complete package result omitted expected project '$expectedProject'."
        }
    }
}

function Assert-VulnerabilityResults {
    param(
        [object]$Audit,
        [Text.Json.JsonDocument]$Document,
        [Collections.Generic.Dictionary[string, string]]$ExpectedProjects,
        [Collections.Generic.Dictionary[string, string[]]]$ExpectedFrameworks
    )

    Assert-AuditHeader $Audit $Document $true
    Assert-NoAuditProblems $Audit

    $projectsElement = $Document.RootElement.GetProperty("projects")
    if ($projectsElement.ValueKind -ne [Text.Json.JsonValueKind]::Array) {
        Fail-Audit "structured output property 'projects' is not an array."
    }

    $projects = @(Get-JsonArray (Get-JsonProperty $Audit "projects") "projects")
    $projectElements = @($projectsElement.EnumerateArray())
    if ($projects.Count -ne $ExpectedProjects.Count) {
        Fail-Audit "the vulnerability result covered $($projects.Count) project(s), but the solution requires $($ExpectedProjects.Count)."
    }

    $solutionDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($SolutionPath))
    $seenProjects = [Collections.Generic.HashSet[string]]::new($ExpectedProjects.Comparer)
    for ($projectIndex = 0; $projectIndex -lt $projects.Count; $projectIndex++) {
        $project = $projects[$projectIndex]
        $projectElement = $projectElements[$projectIndex]
        if ($null -eq $project -or $project -is [string]) {
            Fail-Audit "the vulnerability result contains a malformed project entry."
        }

        Assert-AllowedProperties $project @("path", "frameworks") "a vulnerability project entry"
        $projectPathValue = Get-JsonProperty $project "path"
        if ($projectPathValue -isnot [string] -or [string]::IsNullOrWhiteSpace($projectPathValue)) {
            Fail-Audit "the vulnerability result contains a project without a path."
        }

        $projectPath = Normalize-ProjectPath $projectPathValue $solutionDirectory
        if (-not $ExpectedProjects.ContainsKey($projectPath)) {
            Fail-Audit "the vulnerability result contains unrelated project '$projectPathValue'."
        }

        if (-not $seenProjects.Add($projectPath)) {
            Fail-Audit "the vulnerability result contains duplicate project '$projectPathValue'."
        }

        # dotnet list package --vulnerable intentionally omits framework/package
        # details for projects with no advisories. Complete package coverage was
        # proven by the separate non-vulnerable package graph above.
        $frameworksProperty = Get-JsonProperty $project "frameworks" $false
        $frameworksElement = Get-OptionalJsonArrayElement $projectElement "frameworks" "frameworks for '$projectPathValue'"
        if ($null -eq $frameworksProperty -and $null -eq $frameworksElement) {
            continue
        }

        $frameworks = @(Get-JsonArray $frameworksProperty "frameworks for '$projectPathValue'")
        $frameworkElements = @($frameworksElement.EnumerateArray())
        if ($frameworks.Count -eq 0) {
            Fail-Audit "the vulnerability result contains an empty framework result for '$projectPathValue'."
        }

        $expectedFrameworkNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($expectedFramework in $ExpectedFrameworks[$projectPath]) {
            $expectedFrameworkNames.Add($expectedFramework) | Out-Null
        }

        $seenFrameworks = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        for ($frameworkIndex = 0; $frameworkIndex -lt $frameworks.Count; $frameworkIndex++) {
            $framework = $frameworks[$frameworkIndex]
            $frameworkElement = $frameworkElements[$frameworkIndex]
            if ($null -eq $framework -or $framework -is [string]) {
                Fail-Audit "the vulnerability result contains a malformed framework entry."
            }

            Assert-AllowedProperties $framework @("framework", "topLevelPackages", "transitivePackages") "a vulnerability framework entry"
            $frameworkName = Get-JsonProperty $framework "framework"
            if ($frameworkName -isnot [string] -or [string]::IsNullOrWhiteSpace($frameworkName) -or
                -not $expectedFrameworkNames.Contains($frameworkName) -or
                -not $seenFrameworks.Add($frameworkName)) {
                Fail-Audit "the vulnerability result contains a missing, mismatched, or duplicate framework for '$projectPathValue'."
            }

            $topLevelContext = "topLevelPackages for '$projectPathValue'/$frameworkName"
            $transitiveContext = "transitivePackages for '$projectPathValue'/$frameworkName"
            Get-RequiredJsonArrayElement $frameworkElement "topLevelPackages" $topLevelContext | Out-Null
            Get-RequiredJsonArrayElement $frameworkElement "transitivePackages" $transitiveContext | Out-Null
            $topLevelPackages = @(Get-JsonArray (Get-JsonProperty $framework "topLevelPackages") $topLevelContext)
            $transitivePackages = @(Get-JsonArray (Get-JsonProperty $framework "transitivePackages") $transitiveContext)
            if ($topLevelPackages.Count -eq 0 -or $transitivePackages.Count -eq 0) {
                Fail-Audit "the vulnerability result contains incomplete package arrays for '$projectPathValue'/$frameworkName."
            }

            Fail-Audit "the advisory result reported one or more vulnerable packages."
        }
    }

    foreach ($expectedProject in $ExpectedProjects.Keys) {
        if (-not $seenProjects.Contains($expectedProject)) {
            Fail-Audit "the vulnerability result omitted expected project '$expectedProject'."
        }
    }
}

$expectedProjects = Get-ExpectedProjectPaths $SolutionPath
$expectedFrameworks = Get-ExpectedProjectFrameworks $expectedProjects
$completeArguments = @(
    "list",
    $SolutionPath,
    "package",
    "--include-transitive",
    "--format",
    "json",
    "--output-version",
    "1"
)
$vulnerabilityArguments = @(
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

$complete = Invoke-AuditCommand $completeArguments
Write-Output $complete.Output
Assert-CompletePackageCoverage $complete.Json $complete.Document $expectedProjects $expectedFrameworks

$vulnerable = Invoke-AuditCommand $vulnerabilityArguments
Write-Output $vulnerable.Output
Assert-VulnerabilityResults $vulnerable.Json $vulnerable.Document $expectedProjects $expectedFrameworks

Write-Host "Dependency vulnerability audit passed: $($expectedProjects.Count) solution project result(s) have complete direct and transitive package coverage, and the vulnerability-only result reported no advisories."
