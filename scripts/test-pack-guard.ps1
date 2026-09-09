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
    Assert-Contract (Test-Path -LiteralPath (Join-Path $normalOutputPath "KeelMatrix.FixtureVault.0.1.0.nupkg")) "Normal pack did not produce the expected package."
    Write-Host "Normal pack passed after both pack-guard trip files were removed."
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
