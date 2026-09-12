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

function Invoke-PackageInspection {
    param(
        [string]$InspectionScriptPath,
        [string]$PackagePath
    )

    $output = @(& pwsh -NoProfile -File $InspectionScriptPath -PackagePath $PackagePath -ExpectedVersion "0.1.0" 2>&1)
    [PSCustomObject]@{
        ExitCode = $LASTEXITCODE
        Output = ($output -join [Environment]::NewLine)
    }
}

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
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
    & pwsh -NoProfile -File $inspectionScriptPath -PackagePath $normalPackagePath -SymbolsPackagePath $normalSymbolsPackagePath -ExpectedVersion "0.1.0"
    Assert-Contract ($LASTEXITCODE -eq 0) "Normal pack archives failed package-content inspection."

    $copyrightProbes = [ordered]@{
        "missing" = $null
        "wrong" = "Other"
        "differently-cased" = "keelmatrix"
    }
    foreach ($probe in $copyrightProbes.GetEnumerator()) {
        $probePackagePath = Join-Path $workRoot ("copyright-" + $probe.Key + ".nupkg")
        New-PackageWithCopyright $normalPackagePath $probePackagePath $probe.Value
        $probeResult = Invoke-PackageInspection $inspectionScriptPath $probePackagePath
        Assert-Contract ($probeResult.ExitCode -ne 0) "Package inspection unexpectedly accepted a $($probe.Key) copyright."
        Assert-Contract ($probeResult.Output.Contains("Package copyright must be exactly KeelMatrix.", [StringComparison]::Ordinal)) "Package inspection rejected a $($probe.Key) copyright without the copyright contract error."
        Write-Host "Package inspection rejected $($probe.Key) copyright as expected."
    }

    Write-Host "Normal pack passed after both real and synthetic pack-guard trip files were removed; both archives passed inspection."
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
