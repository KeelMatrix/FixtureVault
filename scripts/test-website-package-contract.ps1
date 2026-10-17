[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$SkipFixtureTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$canonicalRepositoryUrl = "https://github.com/KeelMatrix/FixtureVault"
$visibilitySentinels = @("keelmatrix-public-product", "keelmatrix-internal-package")
$roleSentinels = @("keelmatrix-primary", "keelmatrix-component")
$propertyNames = @(
    "IsPackable",
    "PackageId",
    "PackageTags",
    "Description",
    "Authors",
    "PackageLicenseExpression",
    "PackageLicenseFile",
    "PackageReadmeFile",
    "PackageProjectUrl",
    "RepositoryUrl",
    "RepositoryType",
    "PublishRepositoryUrl",
    "PackageType",
    "PackAsTool",
    "ToolCommandName"
) -join ","

function Assert-Contract {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Test-OrdinalEqual {
    param([AllowNull()][string]$Left, [AllowNull()][string]$Right)
    return [string]::Equals($Left, $Right, [StringComparison]::Ordinal)
}

function Assert-ExactJsonProperties {
    param(
        [System.Text.Json.JsonElement]$Element,
        [string[]]$Expected,
        [string]$Context
    )

    Assert-Contract ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) "$Context must be a JSON object."
    $actual = [System.Collections.Generic.List[string]]::new()
    foreach ($property in $Element.EnumerateObject()) {
        Assert-Contract (-not $actual.Contains($property.Name)) "$Context contains duplicate property '$($property.Name)'."
        $actual.Add($property.Name)
    }

    foreach ($name in $Expected) {
        Assert-Contract ($actual.Contains($name)) "$Context is missing property '$name'."
    }
    foreach ($name in $actual) {
        Assert-Contract ($Expected -ccontains $name) "$Context contains unsupported property '$name'."
    }
}

function Get-ProjectProperties {
    param(
        [string]$ProjectPath,
        [string]$WorkingDirectory
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new("dotnet")
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false
    $startInfo.ArgumentList.Add("msbuild")
    $startInfo.ArgumentList.Add($ProjectPath)
    $startInfo.ArgumentList.Add("-getProperty:$propertyNames")
    $startInfo.ArgumentList.Add("-nologo")

    $process = [System.Diagnostics.Process]::Start($startInfo)
    try {
        $standardOutput = $process.StandardOutput.ReadToEnd()
        $standardError = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        Assert-Contract ($process.ExitCode -eq 0) "MSBuild could not evaluate '$ProjectPath'. $standardError $standardOutput"
        $evaluation = $standardOutput | ConvertFrom-Json -AsHashtable
        Assert-Contract ($evaluation -is [System.Collections.IDictionary] -and $evaluation.Properties -is [System.Collections.IDictionary]) "MSBuild returned invalid property data for '$ProjectPath'."
        return $evaluation.Properties
    }
    finally {
        $process.Dispose()
    }
}

function Get-ExactSentinel {
    param(
        [string[]]$Tags,
        [string[]]$Allowed,
        [string]$Kind,
        [string]$PackageId
    )

    $matches = [System.Collections.Generic.List[string]]::new()
    foreach ($tag in $Tags) {
        foreach ($sentinel in $Allowed) {
            if ([string]::Equals($tag, $sentinel, [StringComparison]::OrdinalIgnoreCase)) {
                $matches.Add($tag)
                break
            }
        }
    }

    Assert-Contract ($matches.Count -eq 1) "$PackageId must contain exactly one $Kind website sentinel."
    Assert-Contract ($Allowed -ccontains $matches[0]) "$PackageId has a non-canonical $Kind sentinel '$($matches[0])'."
    return $matches[0]
}

function Get-ManifestPackages {
    param([string]$ManifestPath)

    Assert-Contract (Test-Path -LiteralPath $ManifestPath -PathType Leaf) "Root website manifest is missing: $ManifestPath"
    $document = [System.Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($ManifestPath))
    try {
        $root = $document.RootElement
        Assert-ExactJsonProperties -Element $root -Expected @("schemaVersion", "packages") -Context "Website manifest"
        $schemaVersion = $root.GetProperty("schemaVersion")
        $versionValue = 0
        Assert-Contract ($schemaVersion.ValueKind -eq [System.Text.Json.JsonValueKind]::Number -and $schemaVersion.TryGetInt32([ref]$versionValue) -and $versionValue -eq 1) "Website manifest schemaVersion must be the integer 1."

        $packagesElement = $root.GetProperty("packages")
        Assert-Contract ($packagesElement.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) "Website manifest packages must be an object."
        $packages = [System.Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
        foreach ($packageProperty in $packagesElement.EnumerateObject()) {
            Assert-Contract (-not $packages.ContainsKey($packageProperty.Name)) "Website manifest repeats package '$($packageProperty.Name)'."
            Assert-ExactJsonProperties -Element $packageProperty.Value -Expected @("visibility", "role") -Context "Website manifest entry '$($packageProperty.Name)'"

            $visibilityElement = $packageProperty.Value.GetProperty("visibility")
            $roleElement = $packageProperty.Value.GetProperty("role")
            Assert-Contract ($visibilityElement.ValueKind -eq [System.Text.Json.JsonValueKind]::String) "Website manifest visibility for '$($packageProperty.Name)' must be a string."
            Assert-Contract ($roleElement.ValueKind -eq [System.Text.Json.JsonValueKind]::String) "Website manifest role for '$($packageProperty.Name)' must be a string."
            $visibility = $visibilityElement.GetString()
            $role = $roleElement.GetString()
            Assert-Contract (@("public-product", "internal-package") -ccontains $visibility) "Website manifest visibility for '$($packageProperty.Name)' is invalid."
            Assert-Contract (@("primary", "component") -ccontains $role) "Website manifest role for '$($packageProperty.Name)' is invalid."
            $packages.Add($packageProperty.Name, [pscustomobject]@{ Visibility = $visibility; Role = $role })
        }
        return $packages
    }
    finally {
        $document.Dispose()
    }
}

function Invoke-WebsitePackageValidation {
    param([string]$Root)

    $resolvedRoot = (Resolve-Path -LiteralPath $Root).Path
    $manifestPackages = Get-ManifestPackages (Join-Path $resolvedRoot "keelmatrix.website.json")
    $projects = @(Get-ChildItem -LiteralPath $resolvedRoot -Recurse -File -Filter "*.csproj" | Where-Object {
        $_.FullName -notmatch '[\\/](\.git|artifacts|bin|obj)[\\/]'
    })
    $packablePackages = [System.Collections.Generic.List[object]]::new()
    $packageIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

    foreach ($project in $projects) {
        $properties = Get-ProjectProperties -ProjectPath $project.FullName -WorkingDirectory $resolvedRoot
        $isPackable = [string]$properties.IsPackable
        Assert-Contract (@("true", "false") -icontains $isPackable) "Could not determine whether '$($project.FullName)' is packable."
        if ($isPackable -ieq "false") {
            continue
        }

        $packageId = [string]$properties.PackageId
        Assert-Contract (-not [string]::IsNullOrWhiteSpace($packageId)) "Packable project '$($project.FullName)' has no PackageId."
        Assert-Contract ($packageIds.Add($packageId)) "More than one packable project declares PackageId '$packageId'."

        $description = [string]$properties.Description
        Assert-Contract (-not [string]::IsNullOrWhiteSpace($description)) "$packageId has no package description."
        Assert-Contract (Test-OrdinalEqual ([string]$properties.Authors) "KeelMatrix") "$packageId authors must be exactly KeelMatrix."
        Assert-Contract ((-not [string]::IsNullOrWhiteSpace([string]$properties.PackageLicenseExpression)) -or (-not [string]::IsNullOrWhiteSpace([string]$properties.PackageLicenseFile))) "$packageId must declare license metadata."
        Assert-Contract (Test-OrdinalEqual ([string]$properties.PackageProjectUrl) $canonicalRepositoryUrl) "$packageId PackageProjectUrl must be the canonical GitHub repository root."
        Assert-Contract (Test-OrdinalEqual ([string]$properties.RepositoryUrl) $canonicalRepositoryUrl) "$packageId RepositoryUrl must be the canonical GitHub repository root."
        Assert-Contract (Test-OrdinalEqual ([string]$properties.RepositoryType) "git") "$packageId RepositoryType must be git."
        Assert-Contract (Test-OrdinalEqual ([string]$properties.PublishRepositoryUrl) "true") "$packageId must publish repository provenance."
        Assert-Contract (Test-OrdinalEqual ([string]$properties.PackageType) "DotnetTool") "$packageId must declare PackageType DotnetTool."
        Assert-Contract (Test-OrdinalEqual ([string]$properties.PackAsTool) "true") "$packageId must be packed as a .NET tool."
        Assert-Contract (-not [string]::IsNullOrWhiteSpace([string]$properties.ToolCommandName)) "$packageId must declare a tool command name."

        $readmeName = [string]$properties.PackageReadmeFile
        Assert-Contract (-not [string]::IsNullOrWhiteSpace($readmeName)) "$packageId must declare PackageReadmeFile."
        Assert-Contract (-not [IO.Path]::IsPathRooted($readmeName) -and $readmeName -notmatch '(^|[\\/])\.\.([\\/]|$)') "$packageId PackageReadmeFile must stay within its project directory."
        $projectRoot = [IO.Path]::GetDirectoryName($project.FullName)
        $readmePath = [IO.Path]::GetFullPath((Join-Path $projectRoot $readmeName))
        Assert-Contract (Test-Path -LiteralPath $readmePath -PathType Leaf) "$packageId package README is missing: $readmeName"

        $tags = @(([string]$properties.PackageTags) -split ";")
        $visibilityTag = Get-ExactSentinel -Tags $tags -Allowed $visibilitySentinels -Kind "visibility" -PackageId $packageId
        $roleTag = Get-ExactSentinel -Tags $tags -Allowed $roleSentinels -Kind "role" -PackageId $packageId
        $visibility = if ($visibilityTag -ceq "keelmatrix-public-product") { "public-product" } else { "internal-package" }
        $role = if ($roleTag -ceq "keelmatrix-primary") { "primary" } else { "component" }

        $packablePackages.Add([pscustomobject]@{
            PackageId = $packageId
            Visibility = $visibility
            Role = $role
            ProjectPath = $project.FullName
        })
    }

    Assert-Contract ($packablePackages.Count -gt 0) "No packable package projects were found."
    Assert-Contract ($manifestPackages.Count -eq $packablePackages.Count) "Website manifest package entries must match the packable project set exactly."
    foreach ($package in $packablePackages) {
        Assert-Contract ($manifestPackages.ContainsKey($package.PackageId)) "Website manifest is missing packable package '$($package.PackageId)'."
        $manifestEntry = $manifestPackages[$package.PackageId]
        Assert-Contract (Test-OrdinalEqual $manifestEntry.Visibility $package.Visibility) "$($package.PackageId) visibility sentinels disagree with the website manifest."
        Assert-Contract (Test-OrdinalEqual $manifestEntry.Role $package.Role) "$($package.PackageId) role sentinels disagree with the website manifest."
    }

    $publicPackages = @($packablePackages | Where-Object { $_.Visibility -ceq "public-product" })
    $primaryPackages = @($packablePackages | Where-Object { $_.Role -ceq "primary" })
    Assert-Contract ($primaryPackages.Count -le 1) "A product family may contain at most one primary package."
    if ($publicPackages.Count -gt 0) {
        $publicPrimaries = @($publicPackages | Where-Object { $_.Role -ceq "primary" })
        Assert-Contract ($publicPrimaries.Count -eq 1) "A public product family must contain exactly one primary package."
    }

    Write-Host "Website package contract passed for $($packablePackages.Count) packable project(s)."
}

function New-FixtureProject {
    param(
        [string]$ProjectRoot,
        [string]$PackageId,
        [string]$Tags,
        [string]$Description = "Audit snapshot and golden files for a fixture validation case."
    )

    New-Item -ItemType Directory -Force -Path $ProjectRoot | Out-Null
    $projectContent = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>true</IsPackable>
    <PackageId>$PackageId</PackageId>
    <PackageTags>$Tags</PackageTags>
    <Description>$Description</Description>
    <PackageType>DotnetTool</PackageType>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>fixturevault</ToolCommandName>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
</Project>
"@
    [IO.File]::WriteAllText((Join-Path $ProjectRoot "$($PackageId).csproj"), $projectContent, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $ProjectRoot "README.md"), "# Fixture package`n", [Text.UTF8Encoding]::new($false))
}

function Write-FixtureManifest {
    param([string]$Path, [System.Collections.IDictionary]$Packages)

    $manifest = [ordered]@{ schemaVersion = 1; packages = $Packages }
    $json = $manifest | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function Invoke-FixtureCase {
    param(
        [string]$WorkRoot,
        [string]$Name,
        [bool]$ShouldPass,
        [string]$Mutation
    )

    $fixtureRoot = Join-Path $WorkRoot $Name
    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    $projectRoot = Join-Path $fixtureRoot "src/FixtureVault"
    $tags = "snapshot-testing;keelmatrix-public-product;keelmatrix-primary"
    $projectUrl = $canonicalRepositoryUrl
    $repositoryUrl = $canonicalRepositoryUrl
    $description = "Audit snapshot and golden files for a fixture validation case."
    $readmeName = "README.md"
    $packages = [ordered]@{
        "KeelMatrix.FixtureVault" = [ordered]@{ visibility = "public-product"; role = "primary" }
    }

    switch ($Mutation) {
        "valid" { }
        "valid-internal" {
            $tags = "snapshot-testing;keelmatrix-internal-package;keelmatrix-primary"
            $packages["KeelMatrix.FixtureVault"] = [ordered]@{ visibility = "internal-package"; role = "primary" }
        }
        "nonpackable-project" { }
        "missing-manifest-entry" { $packages.Clear() }
        "stale-manifest-entry" { $packages["KeelMatrix.Other"] = [ordered]@{ visibility = "internal-package"; role = "component" } }
        "missing-visibility" { $tags = "snapshot-testing;keelmatrix-primary" }
        "duplicate-visibility" { $tags += ";keelmatrix-internal-package" }
        "missing-role" { $tags = "snapshot-testing;keelmatrix-public-product" }
        "duplicate-role" { $tags += ";keelmatrix-component" }
        "wrong-case-visibility" { $tags = "snapshot-testing;KEELMATRIX-PUBLIC-PRODUCT;keelmatrix-primary" }
        "wrong-case-role" { $tags = "snapshot-testing;keelmatrix-public-product;KEELMATRIX-PRIMARY" }
        "manifest-disagrees" { $packages["KeelMatrix.FixtureVault"] = [ordered]@{ visibility = "internal-package"; role = "primary" } }
        "invalid-manifest-role" { $packages["KeelMatrix.FixtureVault"] = [ordered]@{ visibility = "public-product"; role = "Primary" } }
        "extra-manifest-property" { $packages["KeelMatrix.FixtureVault"] = [ordered]@{ visibility = "public-product"; role = "primary"; description = "not classification" } }
        "unknown-schema-version" { }
        "duplicate-schema-property" { }
        "public-without-primary" {
            $tags = "snapshot-testing;keelmatrix-public-product;keelmatrix-component"
            $packages["KeelMatrix.FixtureVault"] = [ordered]@{ visibility = "public-product"; role = "component" }
        }
        "duplicate-primary" {
            $secondRoot = Join-Path $fixtureRoot "src/FixtureVault.Cli"
            New-FixtureProject -ProjectRoot $secondRoot -PackageId "KeelMatrix.FixtureVault.Cli" -Tags "keelmatrix-public-product;keelmatrix-primary"
            $packages["KeelMatrix.FixtureVault.Cli"] = [ordered]@{ visibility = "public-product"; role = "primary" }
        }
        "project-url-fragment" { $projectUrl += "#readme" }
        "repository-url-mismatch" { $repositoryUrl = "https://github.com/KeelMatrix/Other" }
        "missing-readme" { $readmeName = "Missing.md" }
        "missing-description" { $description = "" }
        "wrong-package-type" { }
        default { throw "Unknown website package fixture '$Mutation'." }
    }

    $props = @"
<Project>
  <PropertyGroup>
    <Authors>KeelMatrix</Authors>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <PackageProjectUrl>$projectUrl</PackageProjectUrl>
    <RepositoryUrl>$repositoryUrl</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
  </PropertyGroup>
</Project>
"@
    [IO.File]::WriteAllText((Join-Path $fixtureRoot "Directory.Build.props"), $props, [Text.UTF8Encoding]::new($false))
    New-FixtureProject -ProjectRoot $projectRoot -PackageId "KeelMatrix.FixtureVault" -Tags $tags -Description $description
    if ($Mutation -eq "missing-readme") {
        Remove-Item -LiteralPath (Join-Path $projectRoot "README.md") -Force
    }
    if ($Mutation -eq "wrong-package-type") {
        $projectFile = Get-ChildItem -LiteralPath $projectRoot -Filter "*.csproj" -File | Select-Object -First 1
        $content = [IO.File]::ReadAllText($projectFile.FullName).Replace("<PackageType>DotnetTool</PackageType>", "<PackageType>Library</PackageType>")
        [IO.File]::WriteAllText($projectFile.FullName, $content, [Text.UTF8Encoding]::new($false))
    }
    if ($Mutation -eq "nonpackable-project") {
        $testRoot = Join-Path $fixtureRoot "tests/FixtureVault.Tests"
        New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
        [IO.File]::WriteAllText((Join-Path $testRoot "FixtureVault.Tests.csproj"), "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>", [Text.UTF8Encoding]::new($false))
    }

    Write-FixtureManifest -Path (Join-Path $fixtureRoot "keelmatrix.website.json") -Packages $packages
    if ($Mutation -eq "unknown-schema-version") {
        $manifestPath = Join-Path $fixtureRoot "keelmatrix.website.json"
        $manifestText = [IO.File]::ReadAllText($manifestPath).Replace('"schemaVersion": 1', '"schemaVersion": 2')
        [IO.File]::WriteAllText($manifestPath, $manifestText, [Text.UTF8Encoding]::new($false))
    }
    if ($Mutation -eq "duplicate-schema-property") {
        $manifestPath = Join-Path $fixtureRoot "keelmatrix.website.json"
        $replacement = '"schemaVersion": 1,' + [Environment]::NewLine + '  "schemaVersion": 1'
        $manifestText = [IO.File]::ReadAllText($manifestPath).Replace('"schemaVersion": 1', $replacement)
        [IO.File]::WriteAllText($manifestPath, $manifestText, [Text.UTF8Encoding]::new($false))
    }
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new("pwsh")
    $startInfo.WorkingDirectory = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false
    foreach ($argument in @("-NoProfile", "-File", $PSCommandPath, "-RepositoryRoot", $fixtureRoot, "-SkipFixtureTests")) {
        $startInfo.ArgumentList.Add($argument)
    }
    $process = [System.Diagnostics.Process]::Start($startInfo)
    try {
        $output = $process.StandardOutput.ReadToEnd()
        $errorOutput = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $passed = $process.ExitCode -eq 0
        Assert-Contract ($passed -eq $ShouldPass) "Website package fixture '$Name' had exit $($process.ExitCode), expected pass=$ShouldPass. $output $errorOutput"
        Write-Host "Website package fixture passed: $Name"
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-FixtureTests {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $workRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ("fixturevault-website-contract-" + [Guid]::NewGuid().ToString("N"))))
    $tempPrefix = $tempRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Assert-Contract ($workRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) "Website package fixtures must stay under the operating-system temporary directory."
    New-Item -ItemType Directory -Path $workRoot | Out-Null
    try {
        $cases = @(
            [pscustomobject]@{ Name = "valid-public-primary"; Pass = $true; Mutation = "valid" },
            [pscustomobject]@{ Name = "valid-internal-primary"; Pass = $true; Mutation = "valid-internal" },
            [pscustomobject]@{ Name = "nonpackable-project"; Pass = $true; Mutation = "nonpackable-project" },
            [pscustomobject]@{ Name = "missing-manifest-entry"; Pass = $false; Mutation = "missing-manifest-entry" },
            [pscustomobject]@{ Name = "stale-manifest-entry"; Pass = $false; Mutation = "stale-manifest-entry" },
            [pscustomobject]@{ Name = "missing-visibility-sentinel"; Pass = $false; Mutation = "missing-visibility" },
            [pscustomobject]@{ Name = "duplicate-visibility-sentinel"; Pass = $false; Mutation = "duplicate-visibility" },
            [pscustomobject]@{ Name = "missing-role-sentinel"; Pass = $false; Mutation = "missing-role" },
            [pscustomobject]@{ Name = "duplicate-role-sentinel"; Pass = $false; Mutation = "duplicate-role" },
            [pscustomobject]@{ Name = "wrong-case-visibility-sentinel"; Pass = $false; Mutation = "wrong-case-visibility" },
            [pscustomobject]@{ Name = "wrong-case-role-sentinel"; Pass = $false; Mutation = "wrong-case-role" },
            [pscustomobject]@{ Name = "manifest-sentinel-mismatch"; Pass = $false; Mutation = "manifest-disagrees" },
            [pscustomobject]@{ Name = "invalid-manifest-role"; Pass = $false; Mutation = "invalid-manifest-role" },
            [pscustomobject]@{ Name = "extra-manifest-property"; Pass = $false; Mutation = "extra-manifest-property" },
            [pscustomobject]@{ Name = "unknown-schema-version"; Pass = $false; Mutation = "unknown-schema-version" },
            [pscustomobject]@{ Name = "duplicate-schema-property"; Pass = $false; Mutation = "duplicate-schema-property" },
            [pscustomobject]@{ Name = "public-family-without-primary"; Pass = $false; Mutation = "public-without-primary" },
            [pscustomobject]@{ Name = "duplicate-family-primary"; Pass = $false; Mutation = "duplicate-primary" },
            [pscustomobject]@{ Name = "project-url-fragment"; Pass = $false; Mutation = "project-url-fragment" },
            [pscustomobject]@{ Name = "repository-url-mismatch"; Pass = $false; Mutation = "repository-url-mismatch" },
            [pscustomobject]@{ Name = "missing-package-readme"; Pass = $false; Mutation = "missing-readme" },
            [pscustomobject]@{ Name = "missing-description"; Pass = $false; Mutation = "missing-description" },
            [pscustomobject]@{ Name = "wrong-package-type"; Pass = $false; Mutation = "wrong-package-type" }
        )
        foreach ($case in $cases) {
            Invoke-FixtureCase -WorkRoot $workRoot -Name $case.Name -ShouldPass $case.Pass -Mutation $case.Mutation
        }
    }
    finally {
        $resolvedWorkRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $workRoot).Path)
        Assert-Contract (Test-OrdinalEqual $resolvedWorkRoot $workRoot) "Refusing to remove an unexpected website fixture path."
        Assert-Contract ($resolvedWorkRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) "Refusing to remove website fixtures outside the operating-system temporary directory."
        Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force
    }
}

$originalLocation = Get-Location
try {
    $resolvedRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
    Set-Location -LiteralPath $resolvedRoot
    Invoke-WebsitePackageValidation -Root $resolvedRoot
    if (-not $SkipFixtureTests) {
        Invoke-FixtureTests
    }
    Write-Host "Website package metadata validation completed."
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
finally {
    Set-Location -LiteralPath $originalLocation
}
