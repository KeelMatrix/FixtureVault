[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [string]$ExpectedVersion = "0.1.0",

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ExpectedCommit,

    [string]$SymbolsPackagePath
)

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

$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
Assert-Contract ([IO.Path]::GetExtension($resolvedPackage) -eq ".nupkg") "Package inspection requires a .nupkg file."
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$projectReadmePath = Join-Path $repositoryRoot "src/KeelMatrix.FixtureVault/README.md"
Assert-Contract (Test-Path -LiteralPath $projectReadmePath -PathType Leaf) "Project-local package README is missing: $projectReadmePath"
$expectedReadmeBytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $projectReadmePath).Path)

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($resolvedPackage)
try {
    $entries = @($archive.Entries | ForEach-Object { $_.FullName })
    $duplicateEntries = @($entries | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
    Assert-Contract ($duplicateEntries.Count -eq 0) "Duplicate package entries are not allowed: $($duplicateEntries -join ', ')"

    $requiredPackageEntries = @(
        "README.md",
        "LICENSE/LICENSE",
        "icon.png",
        "KeelMatrix.FixtureVault.nuspec",
        "tools/net8.0/any/DotnetToolSettings.xml",
        "tools/net8.0/any/KeelMatrix.FixtureVault.dll",
        "tools/net8.0/any/KeelMatrix.Redaction.dll",
        "tools/net8.0/any/KeelMatrix.Telemetry.dll"
    )
    foreach ($requiredEntry in $requiredPackageEntries) {
    Assert-Contract ($entries -contains $requiredEntry) "Expected package entry is missing: $requiredEntry"
        $requiredArchiveEntry = $archive.Entries | Where-Object { $_.FullName -eq $requiredEntry } | Select-Object -First 1
        Assert-Contract ($requiredArchiveEntry.Length -gt 0) "Required package entry is empty: $requiredEntry"
    }

    $readmeEntry = $archive.Entries | Where-Object { $_.FullName -eq "README.md" } | Select-Object -First 1
    $readmeStream = $readmeEntry.Open()
    try {
        $packedReadmeBytes = [IO.MemoryStream]::new()
        try {
            $readmeStream.CopyTo($packedReadmeBytes)
            $actualReadmeBytes = $packedReadmeBytes.ToArray()
        }
        finally {
            $packedReadmeBytes.Dispose()
        }
    }
    finally {
        $readmeStream.Dispose()
    }

    $readmeMatches = $expectedReadmeBytes.Length -eq $actualReadmeBytes.Length
    if ($readmeMatches) {
        for ($index = 0; $index -lt $expectedReadmeBytes.Length; $index++) {
            if ($expectedReadmeBytes[$index] -ne $actualReadmeBytes[$index]) {
                $readmeMatches = $false
                break
            }
        }
    }
    Assert-Contract $readmeMatches "Packed README.md does not match the project-local README at $projectReadmePath."

    $toolSettingsEntry = $archive.Entries | Where-Object { $_.FullName -eq "tools/net8.0/any/DotnetToolSettings.xml" } | Select-Object -First 1
    $toolSettingsReader = [IO.StreamReader]::new($toolSettingsEntry.Open())
    try {
        [xml]$toolSettings = $toolSettingsReader.ReadToEnd()
    }
    finally {
        $toolSettingsReader.Dispose()
    }
    $toolCommand = $toolSettings.SelectSingleNode("/DotNetCliTool/Commands/Command[@Name='fixturevault']")
    Assert-Contract ($null -ne $toolCommand -and $toolCommand.EntryPoint -eq "KeelMatrix.FixtureVault.dll" -and $toolCommand.Runner -eq "dotnet") "DotnetTool metadata must expose the fixturevault command."

    Assert-Contract (-not ($entries | Where-Object { $_ -like "tools/netstandard*" })) "The package must contain only the net8.0 tool layout."

    $allowedEntries = @(
        "_rels/.rels",
        "KeelMatrix.FixtureVault.nuspec",
        "tools/net8.0/any/DotnetToolSettings.xml",
        "tools/net8.0/any/KeelMatrix.FixtureVault.dll",
        "tools/net8.0/any/KeelMatrix.FixtureVault.runtimeconfig.json",
        "tools/net8.0/any/KeelMatrix.FixtureVault.pdb",
        "tools/net8.0/any/KeelMatrix.Redaction.dll",
        "tools/net8.0/any/KeelMatrix.Telemetry.dll",
        "tools/net8.0/any/KeelMatrix.FixtureVault.xml",
        "tools/net8.0/any/KeelMatrix.FixtureVault.deps.json",
        "README.md",
        "LICENSE/LICENSE",
        "icon.png",
        "[Content_Types].xml"
    )
    $unexpectedEntries = @($entries | Where-Object {
        ($_ -notin $allowedEntries) -and ($_ -notmatch '^package/services/metadata/core-properties/[^/]+\.psmdcp$')
    })
    Assert-Contract ($unexpectedEntries.Count -eq 0) "Unexpected package entries: $($unexpectedEntries -join ', ')"

    $forbiddenEntries = @($entries | Where-Object {
        $_ -match '(^|/)(\.gitignore|NuGet\.config|global\.json|AGENTS\.md|CHANGELOG\.md|SECURITY\.md|PRIVACY\.md|KeelMatrix\.FixtureVault\.md|\.fixturevault(\.manifest)?\.json|\.env[^/]*|keelmatrix\.telemetry\.json)$' -or
        $_ -match '\.(csproj|sln|yml|yaml|trx)$' -or
        $_ -match '(^|/)(tests?|artifacts|bin|obj|TestResults)(/|$)' -or
        $_ -match '(secret|credential|password)'
    })
    Assert-Contract ($forbiddenEntries.Count -eq 0) "Forbidden repository or secret entries were packed: $($forbiddenEntries -join ', ')"

    $nuspecEntry = $archive.Entries | Where-Object { $_.FullName -eq "KeelMatrix.FixtureVault.nuspec" } | Select-Object -First 1
    $nuspecReader = [IO.StreamReader]::new($nuspecEntry.Open())
    try {
        [xml]$nuspec = $nuspecReader.ReadToEnd()
    }
    finally {
        $nuspecReader.Dispose()
    }

    $namespace = [System.Xml.XmlNamespaceManager]::new($nuspec.NameTable)
    $namespace.AddNamespace("n", $nuspec.DocumentElement.NamespaceURI)
    $metadata = $nuspec.SelectSingleNode("/n:package/n:metadata", $namespace)
    Assert-Contract ($null -ne $metadata) "The package metadata is missing."
    Assert-Contract ($metadata.id -eq "KeelMatrix.FixtureVault") "Package id is not KeelMatrix.FixtureVault."
    Assert-Contract ($metadata.version -eq $ExpectedVersion) "Package version is $($metadata.version), expected $ExpectedVersion."
    Assert-Contract ($metadata.authors -eq "KeelMatrix") "Package authors must be KeelMatrix."
    Assert-Contract ($null -ne $metadata.copyright -and ([string]$metadata.copyright) -ceq "KeelMatrix") "Package copyright must be exactly KeelMatrix."
    Assert-Contract ($metadata.license.type -eq "expression" -and $metadata.license.InnerText -eq "MIT") "Package license must be the MIT expression."
    Assert-Contract ($metadata.readme -eq "README.md") "NuGet README metadata must point to README.md."
    Assert-Contract ($metadata.icon -eq "icon.png") "NuGet icon metadata must point to icon.png."
    Assert-Contract ($metadata.repository.type -eq "git" -and $metadata.repository.url -eq "https://github.com/KeelMatrix/FixtureVault") "Repository metadata is incorrect."
    $packageTypes = @($metadata.packageTypes.packageType | ForEach-Object { $_.name })
    Assert-Contract ($packageTypes.Count -eq 1 -and $packageTypes[0] -eq "DotnetTool") "Package type must be DotnetTool."

    $dependencyGroups = @($metadata.SelectNodes("n:dependencies/n:group", $namespace))
    Assert-Contract ($dependencyGroups.Count -eq 1) "The nuspec must contain exactly one dependency group."
    Assert-Contract ($dependencyGroups[0].targetFramework -eq "net8.0") "The dependency group must target net8.0."

    $dependencyNodes = @($dependencyGroups[0].SelectNodes("n:dependency", $namespace))
    $dependencyIds = @($dependencyNodes | ForEach-Object { $_.id } | Sort-Object)
    Assert-Contract (($dependencyIds -join ",") -eq "KeelMatrix.Redaction,KeelMatrix.Telemetry") "Unexpected nuspec dependency set: $($dependencyIds -join ', ')."
    foreach ($dependency in $dependencyNodes) {
        $expectedDependencyVersion = if ($dependency.id -eq "KeelMatrix.Telemetry") { "[0.1.1]" } else { "[0.1.0]" }
        Assert-Contract ($dependency.version -eq $expectedDependencyVersion) "Dependency $($dependency.id) must be pinned to $expectedDependencyVersion."
        Assert-Contract ($dependency.exclude -eq "Build,Analyzers") "Dependency $($dependency.id) must exclude Build and Analyzers assets."
    }

    $toolAssemblies = @($entries | Where-Object { $_ -like "tools/net8.0/any/*.dll" })
    $expectedAssemblies = @(
        "tools/net8.0/any/KeelMatrix.FixtureVault.dll",
        "tools/net8.0/any/KeelMatrix.Redaction.dll",
        "tools/net8.0/any/KeelMatrix.Telemetry.dll"
    )
    $unexpectedAssemblies = @($toolAssemblies | Where-Object { $_ -notin $expectedAssemblies })
    Assert-Contract ($unexpectedAssemblies.Count -eq 0) "Unexpected tool dependency assemblies: $($unexpectedAssemblies -join ', ')"
    Assert-Contract ((($toolAssemblies | Sort-Object) -join ",") -eq (($expectedAssemblies | Sort-Object) -join ",")) "The tool dependency set must be FixtureVault, Redaction, and Telemetry only."

    $repositoryCommit = [string]$metadata.repository.commit
    Assert-Contract ($repositoryCommit -eq $ExpectedCommit) "Package repository commit is '$repositoryCommit', expected '$ExpectedCommit'."
    $primaryPackageId = [string]$metadata.id
    $primaryPackageVersion = [string]$metadata.version
    $primaryRepositoryType = [string]$metadata.repository.type
    $primaryRepositoryUrl = [string]$metadata.repository.url
    $primaryRepositoryCommit = $repositoryCommit

    $pdbEntry = $archive.Entries | Where-Object { $_.FullName -eq "tools/net8.0/any/KeelMatrix.FixtureVault.pdb" } | Select-Object -First 1
    Assert-Contract ($null -ne $pdbEntry) "The package is missing the FixtureVault PDB required for SourceLink provenance inspection."
    $pdbStream = $pdbEntry.Open()
    try {
        $pdbBytes = [IO.MemoryStream]::new()
        try {
            $pdbStream.CopyTo($pdbBytes)
            $primaryPdbBytes = $pdbBytes.ToArray()
            $pdbText = [Text.Encoding]::UTF8.GetString($primaryPdbBytes)
        }
        finally {
            $pdbBytes.Dispose()
        }
    }
    finally {
        $pdbStream.Dispose()
    }

    $expectedSourceLink = "https://raw.githubusercontent.com/KeelMatrix/FixtureVault/$ExpectedCommit/*"
    Assert-Contract ($pdbText.Contains($expectedSourceLink, [StringComparison]::Ordinal)) "FixtureVault PDB SourceLink does not point at the expected repository commit '$ExpectedCommit'."

    Write-Host "Package contract passed: $([IO.Path]::GetFileName($resolvedPackage))"
}
finally {
    $archive.Dispose()
}

if (-not [string]::IsNullOrWhiteSpace($SymbolsPackagePath)) {
    $resolvedSymbolsPackage = (Resolve-Path -LiteralPath $SymbolsPackagePath).Path
    $expectedSymbolsName = "KeelMatrix.FixtureVault.$ExpectedVersion.snupkg"
    Assert-Contract ([IO.Path]::GetFileName($resolvedSymbolsPackage) -eq $expectedSymbolsName) "Expected exactly $expectedSymbolsName."

    $symbolsArchive = [IO.Compression.ZipFile]::OpenRead($resolvedSymbolsPackage)
    try {
        $symbolEntries = @($symbolsArchive.Entries | ForEach-Object { $_.FullName })
        $duplicateSymbolEntries = @($symbolEntries | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
        Assert-Contract ($duplicateSymbolEntries.Count -eq 0) "Duplicate symbol package entries are not allowed: $($duplicateSymbolEntries -join ', ')"
        $allowedSymbolEntries = @(
            "_rels/.rels",
            "KeelMatrix.FixtureVault.nuspec",
            "tools/net8.0/any/KeelMatrix.FixtureVault.pdb",
            "[Content_Types].xml"
        )
        foreach ($requiredSymbolEntry in $allowedSymbolEntries) {
            Assert-Contract ($symbolEntries -contains $requiredSymbolEntry) "Expected symbol package entry is missing: $requiredSymbolEntry"
            $requiredSymbolArchiveEntry = $symbolsArchive.Entries | Where-Object { $_.FullName -eq $requiredSymbolEntry } | Select-Object -First 1
            Assert-Contract ($requiredSymbolArchiveEntry.Length -gt 0) "Required symbol package entry is empty: $requiredSymbolEntry"
        }
        $unexpectedSymbolEntries = @($symbolEntries | Where-Object {
            ($_ -notin $allowedSymbolEntries) -and ($_ -notmatch '^package/services/metadata/core-properties/[^/]+\.psmdcp$')
        })
        Assert-Contract ($unexpectedSymbolEntries.Count -eq 0) "Unexpected symbol package entries: $($unexpectedSymbolEntries -join ', ')"
        $forbiddenSymbolEntries = @($symbolEntries | Where-Object {
            $_ -match '(^|/)(\.gitignore|NuGet\.config|global\.json|AGENTS\.md|CHANGELOG\.md|SECURITY\.md|PRIVACY\.md|KeelMatrix\.FixtureVault\.md|\.fixturevault(\.manifest)?\.json|\.env[^/]*|keelmatrix\.telemetry\.json)$' -or
            $_ -match '\.(csproj|sln|yml|yaml|trx)$' -or
            $_ -match '(^|/)(tests?|artifacts|bin|obj|TestResults)(/|$)' -or
            $_ -match '(secret|credential|password)'
        })
        Assert-Contract ($forbiddenSymbolEntries.Count -eq 0) "Forbidden entries were packed in the symbol package: $($forbiddenSymbolEntries -join ', ')"

        $symbolNuspecEntry = $symbolsArchive.Entries | Where-Object { $_.FullName -eq "KeelMatrix.FixtureVault.nuspec" } | Select-Object -First 1
        $symbolNuspecReader = [IO.StreamReader]::new($symbolNuspecEntry.Open())
        try {
            [xml]$symbolNuspec = $symbolNuspecReader.ReadToEnd()
        }
        finally {
            $symbolNuspecReader.Dispose()
        }
        $symbolMetadata = $symbolNuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
        Assert-Contract ($null -ne $symbolMetadata) "The symbol package metadata is missing."
        Assert-Contract ([string]$symbolMetadata.id -eq $primaryPackageId) "Symbol package id '$($symbolMetadata.id)' does not match the primary package id '$primaryPackageId'."
        Assert-Contract ([string]$symbolMetadata.version -eq $ExpectedVersion -and [string]$symbolMetadata.version -eq $primaryPackageVersion) "Symbol package version '$($symbolMetadata.version)' does not match the intended release '$ExpectedVersion'."
        Assert-Contract ([string]$symbolMetadata.repository.type -eq $primaryRepositoryType -and
            [string]$symbolMetadata.repository.url -eq $primaryRepositoryUrl -and
            [string]$symbolMetadata.repository.commit -eq $primaryRepositoryCommit) "Symbol package repository provenance does not match the primary package."

        $symbolPdbEntry = $symbolsArchive.Entries | Where-Object { $_.FullName -eq "tools/net8.0/any/KeelMatrix.FixtureVault.pdb" } | Select-Object -First 1
        $symbolPdbStream = $symbolPdbEntry.Open()
        try {
            $symbolPdbBytesStream = [IO.MemoryStream]::new()
            try {
                $symbolPdbStream.CopyTo($symbolPdbBytesStream)
                $symbolPdbBytes = $symbolPdbBytesStream.ToArray()
            }
            finally {
                $symbolPdbBytesStream.Dispose()
            }
        }
        finally {
            $symbolPdbStream.Dispose()
        }
        Assert-Contract ([Convert]::ToBase64String($symbolPdbBytes) -eq [Convert]::ToBase64String($primaryPdbBytes)) "Symbol PDB does not match the primary package PDB."
        Write-Host "Symbol package contract passed: $([IO.Path]::GetFileName($resolvedSymbolsPackage))"
    }
    finally {
        $symbolsArchive.Dispose()
    }
}
