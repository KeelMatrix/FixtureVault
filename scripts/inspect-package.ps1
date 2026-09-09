[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [string]$ExpectedVersion = "0.1.0",

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

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($resolvedPackage)
try {
    $entries = @($archive.Entries | ForEach-Object { $_.FullName })

    foreach ($requiredEntry in @(
        "README.md",
        "LICENSE/LICENSE",
        "icon.png",
        "KeelMatrix.FixtureVault.nuspec",
        "tools/net8.0/any/DotnetToolSettings.xml",
        "tools/net8.0/any/KeelMatrix.FixtureVault.dll",
        "tools/net8.0/any/KeelMatrix.Redaction.dll",
        "tools/net8.0/any/KeelMatrix.Telemetry.dll")) {
    Assert-Contract ($entries -contains $requiredEntry) "Expected package entry is missing: $requiredEntry"
    }

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
        Assert-Contract ($dependency.version -eq "[0.1.0]") "Dependency $($dependency.id) must be pinned to [0.1.0]."
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
        $allowedSymbolEntries = @(
            "_rels/.rels",
            "KeelMatrix.FixtureVault.nuspec",
            "tools/net8.0/any/KeelMatrix.FixtureVault.pdb",
            "[Content_Types].xml"
        )
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
        Write-Host "Symbol package contract passed: $([IO.Path]::GetFileName($resolvedSymbolsPackage))"
    }
    finally {
        $symbolsArchive.Dispose()
    }
}
