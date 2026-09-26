[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-Contract {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-Pack {
    param(
        [string[]]$Arguments
    )

    $outputLines = & dotnet @Arguments 2>&1
    [PSCustomObject]@{
        ExitCode = $LASTEXITCODE
        Output = ($outputLines -join [Environment]::NewLine)
    }
}

function New-PackageWithCopyright {
    param(
        [string]$SourcePackagePath,
        [string]$DestinationPackagePath,
        [AllowNull()]
        [string]$Copyright
    )

    Copy-Item -LiteralPath $SourcePackagePath -Destination $DestinationPackagePath
    $archive = [IO.Compression.ZipFile]::Open($DestinationPackagePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq "KeelMatrix.FixtureVault.nuspec" } | Select-Object -First 1
        Assert-Contract ($null -ne $entry) "The package copyright probe could not find the nuspec entry."

        $reader = [IO.StreamReader]::new($entry.Open())
        try {
            [xml]$nuspec = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $copyrightNode = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='copyright']")
        if ($null -eq $Copyright) {
            Assert-Contract ($null -ne $copyrightNode) "The package copyright probe source is missing its copyright node."
            $copyrightNode.ParentNode.RemoveChild($copyrightNode) | Out-Null
        }
        else {
            Assert-Contract ($null -ne $copyrightNode) "The package copyright probe source is missing its copyright node."
            $copyrightNode.InnerText = $Copyright
        }

        $settings = [Xml.XmlWriterSettings]::new()
        $settings.Encoding = [Text.UTF8Encoding]::new($false)
        $settings.Indent = $true
        $xmlStream = [IO.MemoryStream]::new()
        try {
            $writer = [Xml.XmlWriter]::Create($xmlStream, $settings)
            try {
                $nuspec.Save($writer)
            }
            finally {
                $writer.Dispose()
            }

            $nuspecBytes = $xmlStream.ToArray()
        }
        finally {
            $xmlStream.Dispose()
        }

        $entry.Delete()
        $replacement = $archive.CreateEntry("KeelMatrix.FixtureVault.nuspec")
        $replacementStream = $replacement.Open()
        try {
            $replacementStream.Write($nuspecBytes, 0, $nuspecBytes.Length)
        }
        finally {
            $replacementStream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function New-PackageWithReadme {
    param(
        [string]$SourcePackagePath,
        [string]$DestinationPackagePath,
        [string]$ReadmeContent
    )

    Copy-Item -LiteralPath $SourcePackagePath -Destination $DestinationPackagePath
    $archive = [IO.Compression.ZipFile]::Open($DestinationPackagePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq "README.md" } | Select-Object -First 1
        Assert-Contract ($null -ne $entry) "The package README probe source is missing its README.md entry."
        $entry.Delete()

        $replacement = $archive.CreateEntry("README.md")
        $replacementStream = $replacement.Open()
        try {
            $readmeBytes = [Text.UTF8Encoding]::new($false).GetBytes($ReadmeContent)
            $replacementStream.Write($readmeBytes, 0, $readmeBytes.Length)
        }
        finally {
            $replacementStream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function New-PackageWithoutReadme {
    param(
        [string]$SourcePackagePath,
        [string]$DestinationPackagePath
    )

    Copy-Item -LiteralPath $SourcePackagePath -Destination $DestinationPackagePath
    $archive = [IO.Compression.ZipFile]::Open($DestinationPackagePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq "README.md" } | Select-Object -First 1
        Assert-Contract ($null -ne $entry) "The package README probe source is missing its README.md entry."
        $entry.Delete()
    }
    finally {
        $archive.Dispose()
    }
}

function Copy-ZipWithoutEntry {
    param(
        [string]$SourcePath,
        [string]$DestinationPath,
        [string]$EntryName
    )

    Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath
    $archive = [IO.Compression.ZipFile]::Open($DestinationPath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -eq $EntryName })
        Assert-Contract ($entries.Count -gt 0) "The archive mutation source is missing $EntryName."
        $entries | ForEach-Object { $_.Delete() }
    }
    finally { $archive.Dispose() }
}

function Copy-ZipWithEntryBytes {
    param(
        [string]$SourcePath,
        [string]$DestinationPath,
        [string]$EntryName,
        [byte[]]$Bytes
    )

    Copy-ZipWithoutEntry $SourcePath $DestinationPath $EntryName
    $archive = [IO.Compression.ZipFile]::Open($DestinationPath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.CreateEntry($EntryName)
        $stream = $entry.Open()
        try {
            if ($Bytes.Length -gt 0) { $stream.Write($Bytes, 0, $Bytes.Length) }
        }
        finally { $stream.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Copy-ZipWithDuplicateEntry {
    param(
        [string]$SourcePath,
        [string]$DestinationPath,
        [string]$EntryName
    )

    Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath
    $archive = [IO.Compression.ZipFile]::Open($DestinationPath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $sourceEntry = $archive.Entries | Where-Object { $_.FullName -eq $EntryName } | Select-Object -First 1
        Assert-Contract ($null -ne $sourceEntry) "The archive duplicate mutation source is missing $EntryName."
        $sourceStream = $sourceEntry.Open()
        try {
            $bytes = [IO.MemoryStream]::new()
            try {
                $sourceStream.CopyTo($bytes)
                $duplicate = $archive.CreateEntry($EntryName)
                $duplicateStream = $duplicate.Open()
                try { $duplicateStream.Write($bytes.ToArray(), 0, [int]$bytes.Length) }
                finally { $duplicateStream.Dispose() }
            }
            finally { $bytes.Dispose() }
        }
        finally { $sourceStream.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Copy-ZipWithUnexpectedEntry {
    param(
        [string]$SourcePath,
        [string]$DestinationPath,
        [string]$EntryName
    )

    Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath
    $archive = [IO.Compression.ZipFile]::Open($DestinationPath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $null = $archive.CreateEntry($EntryName)
    }
    finally { $archive.Dispose() }
}

function New-EmptyZipPackage {
    param([string]$DestinationPath)

    $stream = [IO.File]::Open($DestinationPath, [IO.FileMode]::CreateNew)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
        $archive.Dispose()
    }
    finally { $stream.Dispose() }
}

function Copy-ZipWithNuspecMutation {
    param(
        [string]$SourcePath,
        [string]$DestinationPath,
        [string]$Mutation,
        [string]$Value
    )

    Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath
    $archive = [IO.Compression.ZipFile]::Open($DestinationPath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq "KeelMatrix.FixtureVault.nuspec" } | Select-Object -First 1
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $nodePath = switch ($Mutation) {
            "id" { "/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='id']" }
            "version" { "/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='version']" }
            "commit" { "/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='repository']" }
            default { throw "Unknown symbol nuspec mutation: $Mutation" }
        }
        $node = $nuspec.SelectSingleNode($nodePath)
        Assert-Contract ($null -ne $node) "The symbol nuspec mutation source is missing $Mutation."
        if ($Mutation -eq "commit") { $node.SetAttribute("commit", $Value) } else { $node.InnerText = $Value }
        $settings = [Xml.XmlWriterSettings]::new()
        $settings.Encoding = [Text.UTF8Encoding]::new($false)
        $xmlStream = [IO.MemoryStream]::new()
        try {
            $writer = [Xml.XmlWriter]::Create($xmlStream, $settings)
            try { $nuspec.Save($writer) } finally { $writer.Dispose() }
            $bytes = $xmlStream.ToArray()
        }
        finally { $xmlStream.Dispose() }
        $entry.Delete()
        $replacement = $archive.CreateEntry("KeelMatrix.FixtureVault.nuspec")
        $replacementStream = $replacement.Open()
        try { $replacementStream.Write($bytes, 0, $bytes.Length) } finally { $replacementStream.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Invoke-PackageInspection {
    param(
        [string]$InspectionScriptPath,
        [string]$PackagePath,
        [string]$ExpectedCommit,
        [string]$SymbolsPackagePath = ""
    )

    $arguments = @(
        "-NoProfile", "-File", $InspectionScriptPath,
        "-PackagePath", $PackagePath,
        "-ExpectedVersion", "0.1.0",
        "-ExpectedCommit", $ExpectedCommit
    )
    if (-not [string]::IsNullOrWhiteSpace($SymbolsPackagePath)) {
        $arguments += @("-SymbolsPackagePath", $SymbolsPackagePath)
    }
    $output = @(& pwsh @arguments 2>&1)
    [PSCustomObject]@{
        ExitCode = $LASTEXITCODE
        Output = ($output -join [Environment]::NewLine)
    }
}

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$repositoryCommit = (& git -C $repositoryRoot rev-parse HEAD 2>&1 | Out-String).Trim()
Assert-Contract ($LASTEXITCODE -eq 0 -and $repositoryCommit -match '^[0-9a-fA-F]{40}$') "Could not resolve the repository commit for package provenance tests."
$projectPath = Join-Path $repositoryRoot "src/KeelMatrix.FixtureVault/KeelMatrix.FixtureVault.csproj"
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ("fixturevault-pack-guard-" + [Guid]::NewGuid().ToString("N"))
$probeTargetsPath = Join-Path $workRoot "pack-guard-probe.targets"
New-Item -ItemType Directory -Force -Path $workRoot | Out-Null

$probeTargets = @'
<Project>
  <Target Name="InjectFixtureVaultPackGuardProbe" BeforeTargets="RejectForbiddenFilesInPack">
    <ItemGroup>
      <_PackageFiles Include="$(FixtureVaultPackGuardProbe)" />
    </ItemGroup>
  </Target>
</Project>
'@
[IO.File]::WriteAllText($probeTargetsPath, $probeTargets, [Text.UTF8Encoding]::new($false))

try {
    foreach ($tripName in @(".env.trip", ".ENV.trip")) {
        $tripPath = Join-Path $repositoryRoot $tripName
        Assert-Contract (-not (Test-Path -LiteralPath $tripPath)) "Cannot run the real-file pack-guard test because $tripName already exists."
        [IO.File]::WriteAllText($tripPath, "real pack guard regression probe", [Text.UTF8Encoding]::new($false))
        try {
            $negativeOutputPath = Join-Path $workRoot ("real-negative-" + $tripName.TrimStart(".").Replace(".", "-"))
            $result = Invoke-Pack @(
                "pack",
                $projectPath,
                "-c", "Release",
                "--no-build",
                "--no-restore",
                "--output", $negativeOutputPath
            )

            Assert-Contract ($result.ExitCode -ne 0) "Pack unexpectedly succeeded with real repository file $tripName."
            Assert-Contract ($result.Output.Contains("Refusing to pack forbidden repository file(s):")) "Pack failed for real repository file $tripName without the pack-guard error."
            Assert-Contract ($result.Output.Contains($tripPath)) "Pack-guard output did not identify real repository file $tripName."
            $negativeArchives = @(Get-ChildItem -LiteralPath $negativeOutputPath -File -ErrorAction SilentlyContinue)
            Assert-Contract ($negativeArchives.Count -eq 0) "Pack emitted archives after rejecting real repository file $tripName."
            Write-Host "Pack guard rejected real repository file $tripName as expected."
        }
        finally {
            if (Test-Path -LiteralPath $tripPath) {
                Remove-Item -LiteralPath $tripPath -Force
            }
        }

        Assert-Contract (-not (Test-Path -LiteralPath $tripPath)) "$tripName was not removed after its real-file negative test."
    }

    foreach ($tripName in @(".env.trip", ".ENV.trip")) {
        $tripPath = Join-Path $workRoot $tripName
        [IO.File]::WriteAllText($tripPath, "pack guard regression probe", [Text.UTF8Encoding]::new($false))
        try {
            $negativeOutputPath = Join-Path $workRoot ("negative-" + $tripName.TrimStart(".").Replace(".", "-"))
            $probeTargetsAbsolutePath = (Resolve-Path -LiteralPath $probeTargetsPath).Path
            $tripAbsolutePath = (Resolve-Path -LiteralPath $tripPath).Path
            $result = Invoke-Pack @(
                "pack",
                $projectPath,
                "-c", "Release",
                "--no-build",
                "--no-restore",
                "--output", $negativeOutputPath,
                "-p:CustomAfterMicrosoftCommonTargets=$probeTargetsAbsolutePath",
                "-p:FixtureVaultPackGuardProbe=$tripAbsolutePath"
            )

            Assert-Contract ($result.ExitCode -ne 0) "Pack unexpectedly succeeded with $tripName in _PackageFiles."
            Assert-Contract ($result.Output.Contains("Refusing to pack forbidden repository file(s):")) "Pack failed for $tripName without the pack-guard error."
            Assert-Contract ($result.Output.Contains($tripAbsolutePath)) "Pack-guard output did not identify $tripName."
            Write-Host "Pack guard rejected $tripName as expected."
        }
        finally {
            if (Test-Path -LiteralPath $tripPath) {
                Remove-Item -LiteralPath $tripPath -Force
            }
        }

        Assert-Contract (-not (Test-Path -LiteralPath $tripPath)) "$tripName was not removed after its negative test."
    }

    $normalOutputPath = Join-Path $workRoot "normal"
    $normalResult = Invoke-Pack @(
        "pack",
        $projectPath,
        "-c", "Release",
        "--no-build",
        "--no-restore",
        "--output", $normalOutputPath
    )
    Assert-Contract ($normalResult.ExitCode -eq 0) "Normal pack failed after the pack-guard negative tests. $($normalResult.Output)"
    $normalPackagePath = Join-Path $normalOutputPath "KeelMatrix.FixtureVault.0.1.0.nupkg"
    $normalSymbolsPackagePath = Join-Path $normalOutputPath "KeelMatrix.FixtureVault.0.1.0.snupkg"
    Assert-Contract (Test-Path -LiteralPath $normalPackagePath) "Normal pack did not produce the expected package."
    Assert-Contract (Test-Path -LiteralPath $normalSymbolsPackagePath) "Normal pack did not produce the expected symbols package."

    $inspectionScriptPath = Join-Path $repositoryRoot "scripts/inspect-package.ps1"
    & pwsh -NoProfile -File $inspectionScriptPath -PackagePath $normalPackagePath -SymbolsPackagePath $normalSymbolsPackagePath -ExpectedVersion "0.1.0" -ExpectedCommit $repositoryCommit
    Assert-Contract ($LASTEXITCODE -eq 0) "Normal pack archives failed package-content inspection."

    $symbolRequiredEntries = @(
        "_rels/.rels",
        "KeelMatrix.FixtureVault.nuspec",
        "tools/net8.0/any/KeelMatrix.FixtureVault.pdb",
        "[Content_Types].xml"
    )
    $emptySymbolsRoot = Join-Path $workRoot "empty-symbols"
    New-Item -ItemType Directory -Force -Path $emptySymbolsRoot | Out-Null
    $emptySymbolsPath = Join-Path $emptySymbolsRoot "KeelMatrix.FixtureVault.0.1.0.snupkg"
    New-EmptyZipPackage $emptySymbolsPath
    $emptySymbolsResult = Invoke-PackageInspection $inspectionScriptPath $normalPackagePath $repositoryCommit $emptySymbolsPath
    Assert-Contract ($emptySymbolsResult.ExitCode -ne 0) "Package inspection unexpectedly accepted an empty symbols archive."
    Write-Host "Package inspection rejected an empty symbols archive as expected."

    foreach ($requiredSymbolEntry in $symbolRequiredEntries) {
        $missingName = $requiredSymbolEntry.Replace('/', '-').Replace('[', '').Replace(']', '')
        $missingPath = Join-Path $workRoot ("missing-symbol-" + $missingName + ".snupkg")
        Copy-ZipWithoutEntry $normalSymbolsPackagePath $missingPath $requiredSymbolEntry
        $missingResult = Invoke-PackageInspection $inspectionScriptPath $normalPackagePath $repositoryCommit $missingPath
        Assert-Contract ($missingResult.ExitCode -ne 0) "Package inspection unexpectedly accepted symbols missing $requiredSymbolEntry."
    }
    Write-Host "Package inspection rejected every mandatory symbol entry removal as expected."

    $emptyPdbPath = Join-Path $workRoot "empty-symbol-pdb.snupkg"
    Copy-ZipWithEntryBytes $normalSymbolsPackagePath $emptyPdbPath "tools/net8.0/any/KeelMatrix.FixtureVault.pdb" ([byte[]]@())
    $emptyPdbResult = Invoke-PackageInspection $inspectionScriptPath $normalPackagePath $repositoryCommit $emptyPdbPath
    Assert-Contract ($emptyPdbResult.ExitCode -ne 0) "Package inspection unexpectedly accepted an empty symbol PDB."

    $truncatedPdbPath = Join-Path $workRoot "truncated-symbol-pdb.snupkg"
    Copy-ZipWithEntryBytes $normalSymbolsPackagePath $truncatedPdbPath "tools/net8.0/any/KeelMatrix.FixtureVault.pdb" ([byte[]](0x01, 0x02, 0x03))
    $truncatedPdbResult = Invoke-PackageInspection $inspectionScriptPath $normalPackagePath $repositoryCommit $truncatedPdbPath
    Assert-Contract ($truncatedPdbResult.ExitCode -ne 0) "Package inspection unexpectedly accepted a truncated symbol PDB."

    $mismatchedPdbPath = Join-Path $workRoot "mismatched-symbol-pdb.snupkg"
    Copy-ZipWithEntryBytes $normalSymbolsPackagePath $mismatchedPdbPath "tools/net8.0/any/KeelMatrix.FixtureVault.pdb" ([byte[]](0x50, 0x44, 0x42, 0x2D, 0x6D, 0x69, 0x73, 0x6D, 0x61, 0x74, 0x63, 0x68))
    $mismatchedPdbResult = Invoke-PackageInspection $inspectionScriptPath $normalPackagePath $repositoryCommit $mismatchedPdbPath
    Assert-Contract ($mismatchedPdbResult.ExitCode -ne 0) "Package inspection unexpectedly accepted a mismatched symbol PDB."

    $wrongSymbolIdentityCases = @(
        [pscustomobject]@{ Name = "id"; Value = "Other.Package"; Expected = "Symbol package id" },
        [pscustomobject]@{ Name = "version"; Value = "9.9.9"; Expected = "Symbol package version" },
        [pscustomobject]@{ Name = "commit"; Value = ("0" * 40); Expected = "Symbol package repository provenance" }
    )
    foreach ($wrongCase in $wrongSymbolIdentityCases) {
        $wrongPath = Join-Path $workRoot ("wrong-symbol-" + $wrongCase.Name + ".snupkg")
        Copy-ZipWithNuspecMutation $normalSymbolsPackagePath $wrongPath $wrongCase.Name $wrongCase.Value
        $wrongResult = Invoke-PackageInspection $inspectionScriptPath $normalPackagePath $repositoryCommit $wrongPath
        Assert-Contract ($wrongResult.ExitCode -ne 0) "Package inspection unexpectedly accepted a symbol package with wrong $($wrongCase.Name)."
    }
    Write-Host "Package inspection rejected wrong symbol identity, version, and repository commit as expected."

    $duplicateSymbolPath = Join-Path $workRoot "duplicate-symbol-entry.snupkg"
    Copy-ZipWithDuplicateEntry $normalSymbolsPackagePath $duplicateSymbolPath "tools/net8.0/any/KeelMatrix.FixtureVault.pdb"
    $duplicateSymbolResult = Invoke-PackageInspection $inspectionScriptPath $normalPackagePath $repositoryCommit $duplicateSymbolPath
    Assert-Contract ($duplicateSymbolResult.ExitCode -ne 0) "Package inspection unexpectedly accepted duplicate symbol entries."

    $unexpectedSymbolPath = Join-Path $workRoot "unexpected-symbol-entry.snupkg"
    Copy-ZipWithUnexpectedEntry $normalSymbolsPackagePath $unexpectedSymbolPath "unexpected-symbol-entry.txt"
    $unexpectedSymbolResult = Invoke-PackageInspection $inspectionScriptPath $normalPackagePath $repositoryCommit $unexpectedSymbolPath
    Assert-Contract ($unexpectedSymbolResult.ExitCode -ne 0) "Package inspection unexpectedly accepted an unexpected symbol entry."

    $duplicatePrimaryPath = Join-Path $workRoot "duplicate-primary-entry.nupkg"
    Copy-ZipWithDuplicateEntry $normalPackagePath $duplicatePrimaryPath "KeelMatrix.FixtureVault.nuspec"
    $duplicatePrimaryResult = Invoke-PackageInspection $inspectionScriptPath $duplicatePrimaryPath $repositoryCommit
    Assert-Contract ($duplicatePrimaryResult.ExitCode -ne 0) "Package inspection unexpectedly accepted duplicate primary entries."

    $unexpectedPrimaryPath = Join-Path $workRoot "unexpected-primary-entry.nupkg"
    Copy-ZipWithUnexpectedEntry $normalPackagePath $unexpectedPrimaryPath "unexpected-primary-entry.txt"
    $unexpectedPrimaryResult = Invoke-PackageInspection $inspectionScriptPath $unexpectedPrimaryPath $repositoryCommit
    Assert-Contract ($unexpectedPrimaryResult.ExitCode -ne 0) "Package inspection unexpectedly accepted an unexpected primary entry."
    Write-Host "Package inspection rejected duplicate and unexpected entries in both archives, plus symbol PDB mutations."

    $staleCommit = ("0" * 40) -join ""
    $staleProvenancePackagePath = Join-Path $workRoot "stale-provenance.nupkg"
    New-PackageWithCopyright $normalPackagePath $staleProvenancePackagePath "KeelMatrix"
    $staleArchive = [IO.Compression.ZipFile]::Open($staleProvenancePackagePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $staleNuspecEntry = $staleArchive.Entries | Where-Object { $_.FullName -eq "KeelMatrix.FixtureVault.nuspec" } | Select-Object -First 1
        Assert-Contract ($null -ne $staleNuspecEntry) "The stale-provenance probe could not find the nuspec entry."
        $staleNuspecReader = [IO.StreamReader]::new($staleNuspecEntry.Open())
        try {
            [xml]$staleNuspec = $staleNuspecReader.ReadToEnd()
        }
        finally {
            $staleNuspecReader.Dispose()
        }

        $staleRepositoryNode = $staleNuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='repository']")
        Assert-Contract ($null -ne $staleRepositoryNode) "The stale-provenance probe source is missing its repository node."
        $staleRepositoryNode.SetAttribute("commit", $staleCommit)

        $staleSettings = [Xml.XmlWriterSettings]::new()
        $staleSettings.Encoding = [Text.UTF8Encoding]::new($false)
        $staleSettings.Indent = $true
        $staleXmlStream = [IO.MemoryStream]::new()
        try {
            $staleWriter = [Xml.XmlWriter]::Create($staleXmlStream, $staleSettings)
            try {
                $staleNuspec.Save($staleWriter)
            }
            finally {
                $staleWriter.Dispose()
            }

            $staleNuspecBytes = $staleXmlStream.ToArray()
        }
        finally {
            $staleXmlStream.Dispose()
        }

        $staleNuspecEntry.Delete()
        $staleReplacement = $staleArchive.CreateEntry("KeelMatrix.FixtureVault.nuspec")
        $staleReplacementStream = $staleReplacement.Open()
        try {
            $staleReplacementStream.Write($staleNuspecBytes, 0, $staleNuspecBytes.Length)
        }
        finally {
            $staleReplacementStream.Dispose()
        }
    }
    finally {
        $staleArchive.Dispose()
    }

    $staleProvenanceResult = Invoke-PackageInspection $inspectionScriptPath $staleProvenancePackagePath $repositoryCommit
    Assert-Contract ($staleProvenanceResult.ExitCode -ne 0) "Package inspection unexpectedly accepted stale repository provenance."
    Assert-Contract ($staleProvenanceResult.Output.Contains("Package repository commit is", [StringComparison]::Ordinal)) "Stale repository provenance failed without the exact-commit contract error."
    Write-Host "Package inspection rejected stale repository provenance as expected."

    $copyrightProbes = [ordered]@{
        "missing" = $null
        "wrong" = "Other"
        "differently-cased" = "keelmatrix"
    }
    foreach ($probe in $copyrightProbes.GetEnumerator()) {
        $probePackagePath = Join-Path $workRoot ("copyright-" + $probe.Key + ".nupkg")
        New-PackageWithCopyright $normalPackagePath $probePackagePath $probe.Value
        $probeResult = Invoke-PackageInspection $inspectionScriptPath $probePackagePath $repositoryCommit
        Assert-Contract ($probeResult.ExitCode -ne 0) "Package inspection unexpectedly accepted a $($probe.Key) copyright."
        Assert-Contract ($probeResult.Output.Contains("Package copyright must be exactly KeelMatrix.", [StringComparison]::Ordinal)) "Package inspection rejected a $($probe.Key) copyright without the copyright contract error."
        Write-Host "Package inspection rejected $($probe.Key) copyright as expected."
    }

    $missingReadmePackagePath = Join-Path $workRoot "missing-readme.nupkg"
    New-PackageWithoutReadme $normalPackagePath $missingReadmePackagePath
    $missingReadmeResult = Invoke-PackageInspection $inspectionScriptPath $missingReadmePackagePath $repositoryCommit
    Assert-Contract ($missingReadmeResult.ExitCode -ne 0) "Package inspection unexpectedly accepted a package without README.md."
    Assert-Contract ($missingReadmeResult.Output.Contains("Expected package entry is missing: README.md", [StringComparison]::Ordinal)) "Package inspection rejected a package without README.md without the README contract error."
    Write-Host "Package inspection rejected a package without README.md as expected."

    $mismatchedReadmePackagePath = Join-Path $workRoot "mismatched-readme.nupkg"
    New-PackageWithReadme $normalPackagePath $mismatchedReadmePackagePath "This is not the project-local package README."
    $mismatchedReadmeResult = Invoke-PackageInspection $inspectionScriptPath $mismatchedReadmePackagePath $repositoryCommit
    Assert-Contract ($mismatchedReadmeResult.ExitCode -ne 0) "Package inspection unexpectedly accepted a README.md from another source."
    Assert-Contract ($mismatchedReadmeResult.Output.Contains("Packed README.md does not match the project-local README", [StringComparison]::Ordinal)) "Package inspection rejected a mismatched README.md without the provenance contract error."
    Write-Host "Package inspection rejected a mismatched README.md as expected."

    Write-Host "Normal pack passed after both real and synthetic pack-guard trip files were removed; both archives passed inspection."
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
