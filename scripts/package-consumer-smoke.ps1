[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [string]$ExpectedVersion = "0.1.0"
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

function Get-Sha512Base64 {
    param([string]$Path)

    $sha512 = [Security.Cryptography.SHA512]::Create()
    try {
        return [Convert]::ToBase64String($sha512.ComputeHash([IO.File]::ReadAllBytes($Path)))
    }
    finally {
        $sha512.Dispose()
    }
}

function Invoke-CommandCapture {
    param(
        [string]$Executable,
        [string[]]$Arguments,
        [string]$OutputPath
    )

    & $Executable @Arguments *> $OutputPath
    return $LASTEXITCODE
}

$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$expectedPackageName = "KeelMatrix.FixtureVault.$ExpectedVersion.nupkg"
Assert-Contract ([IO.Path]::GetFileName($resolvedPackage) -eq $expectedPackageName) "Expected exactly $expectedPackageName."

$workRoot = Join-Path ([IO.Path]::GetTempPath()) ("fixturevault-consumer-" + [Guid]::NewGuid().ToString("N"))
$feedRoot = Join-Path $workRoot "feed"
$toolRoot = Join-Path $workRoot "tool"
$consumerRoot = Join-Path $workRoot "consumer"
$packageCache = Join-Path $workRoot "packages"
$httpCache = Join-Path $workRoot "http-cache"
$configPath = Join-Path $workRoot "NuGet.config"
New-Item -ItemType Directory -Force -Path $feedRoot, $toolRoot, $consumerRoot, $packageCache, $httpCache | Out-Null

try {
    $feedPackage = Join-Path $feedRoot $expectedPackageName
    Copy-Item -LiteralPath $resolvedPackage -Destination $feedPackage
    Assert-Contract (@(Get-ChildItem -LiteralPath $feedRoot -Filter *.nupkg -File).Count -eq 1) "The isolated local feed must contain exactly one .nupkg."

    $escapedFeedRoot = [Security.SecurityElement]::Escape($feedRoot)
    $config = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="fixturevault-local" value="$escapedFeedRoot" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="fixturevault-local">
      <package pattern="KeelMatrix.FixtureVault" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="KeelMatrix.Redaction" />
      <package pattern="KeelMatrix.Telemetry" />
      <package pattern="Microsoft.*" />
      <package pattern="System.*" />
      <package pattern="runtime.*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@
    [IO.File]::WriteAllText($configPath, $config, [Text.UTF8Encoding]::new($false))

    $env:NUGET_PACKAGES = $packageCache
    $env:NUGET_HTTP_CACHE_PATH = $httpCache
    $installLog = Join-Path $workRoot "tool-install.log"
    $installCode = Invoke-CommandCapture "dotnet" @(
        "tool", "install", "KeelMatrix.FixtureVault",
        "--tool-path", $toolRoot,
        "--version", $ExpectedVersion,
        "--configfile", $configPath,
        "--verbosity", "diagnostic"
    ) $installLog
    if ($installCode -ne 0) {
        $installDetails = if (Test-Path -LiteralPath $installLog) { [IO.File]::ReadAllText($installLog) } else { "No installer output was captured." }
        throw "The packed tool failed to install.`n$installDetails"
    }

    $cachedPackage = @(Get-ChildItem -LiteralPath $toolRoot -Recurse -File | Where-Object { $_.Name -ieq $expectedPackageName })
    if ($cachedPackage.Count -ne 1) {
        throw "The isolated tool store did not contain exactly one resolved FixtureVault package."
    }
    Assert-Contract ((Get-Sha512Base64 $cachedPackage[0].FullName) -eq (Get-Sha512Base64 $feedPackage)) "Resolved FixtureVault package bits did not match the local feed package."
    Write-Host "Resolved KeelMatrix.FixtureVault $ExpectedVersion from the isolated local feed; package hash matches."

    $executableName = "fixturevault"
    if ([OperatingSystem]::IsWindows()) {
        $executableName += ".exe"
    }
    $fixtureVault = Join-Path $toolRoot $executableName
    Assert-Contract (Test-Path -LiteralPath $fixtureVault) "The installed fixturevault executable was not found."

    Push-Location $consumerRoot
    try {
        $helpPath = Join-Path $workRoot "help.txt"
        Assert-Contract ((Invoke-CommandCapture $fixtureVault @("--help") $helpPath) -eq 0) "fixturevault --help failed."
        $help = [IO.File]::ReadAllText($helpPath)
        Assert-Contract ($help -match "Usage:" -and $help -match "fixturevault") "fixturevault --help did not print the expected usage text."

        Assert-Contract ((Invoke-CommandCapture $fixtureVault @("init") (Join-Path $workRoot "init.txt")) -eq 0) "fixturevault init failed."
        Assert-Contract (Test-Path -LiteralPath (Join-Path $consumerRoot ".fixturevault.json")) "fixturevault init did not create .fixturevault.json."
        Assert-Contract ((Invoke-CommandCapture $fixtureVault @("scan") (Join-Path $workRoot "clean-scan.txt")) -eq 0) "Clean fixturevault scan failed."

        $testsRoot = Join-Path $consumerRoot "tests"
        New-Item -ItemType Directory -Force -Path $testsRoot | Out-Null
        [IO.File]::WriteAllText((Join-Path $testsRoot "OrderTests.received.json"), "{`"id`":1}", [Text.UTF8Encoding]::new($false))
        $blockingReportPath = Join-Path $workRoot "blocking-report.json"
        $blockingCode = Invoke-CommandCapture $fixtureVault @("scan", "--format", "json") $blockingReportPath
        Assert-Contract ($blockingCode -eq 1) "Blocking fixturevault scan returned $blockingCode instead of 1."
        $report = [IO.File]::ReadAllText($blockingReportPath) | ConvertFrom-Json
        Assert-Contract ($report.schemaVersion -eq 1) "Blocking scan JSON did not report schema version 1."
        Assert-Contract (@($report.findings | Where-Object { $_.ruleId -eq "FV001" }).Count -gt 0) "Blocking scan JSON did not contain the expected FV001 finding."
    }
    finally {
        Pop-Location
    }

    Write-Host "Consumer smoke passed: help, init, clean scan (0), and blocking JSON scan (1)."
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
