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
    Write-Host "Resolved KeelMatrix.FixtureVault $ExpectedVersion from the isolated local feed using fresh package and HTTP caches plus explicit package-source mapping."

    $executableName = "fixturevault"
    if ([OperatingSystem]::IsWindows()) {
        $executableName += ".exe"
    }
    $fixtureVault = Join-Path $toolRoot $executableName
    Assert-Contract (Test-Path -LiteralPath $fixtureVault) "The installed fixturevault executable was not found."

    Push-Location $consumerRoot
    try {
        [IO.File]::WriteAllBytes(
            (Join-Path $consumerRoot "icon.png"),
            [byte[]](0x89, 0x50, 0x4E, 0x47, 0x00, 0x01))
        $docsRoot = Join-Path $consumerRoot "docs"
        New-Item -ItemType Directory -Force -Path $docsRoot | Out-Null
        [IO.File]::WriteAllBytes(
            (Join-Path $docsRoot "manual.pdf"),
            [byte[]](0x25, 0x50, 0x44, 0x46, 0x00, 0x01))
        [IO.File]::WriteAllBytes(
            (Join-Path $consumerRoot "unrelated.zip"),
            [byte[]](0x50, 0x4B, 0x03, 0x04, 0x00, 0x01))

        $helpPath = Join-Path $workRoot "help.txt"
        Assert-Contract ((Invoke-CommandCapture $fixtureVault @("--help") $helpPath) -eq 0) "fixturevault --help failed."
        $help = [IO.File]::ReadAllText($helpPath)
        Assert-Contract ($help -match "Usage:" -and $help -match "fixturevault" -and $help -match "doubled-quote escapes") "fixturevault --help did not print the expected usage text."

        Assert-Contract ((Invoke-CommandCapture $fixtureVault @("init") (Join-Path $workRoot "init.txt")) -eq 0) "fixturevault init failed."
        Assert-Contract (Test-Path -LiteralPath (Join-Path $consumerRoot ".fixturevault.json")) "fixturevault init did not create .fixturevault.json."
        Assert-Contract ((Invoke-CommandCapture $fixtureVault @("scan") (Join-Path $workRoot "clean-scan.txt")) -eq 0) "Clean fixturevault scan failed."

        $testsRoot = Join-Path $consumerRoot "tests"
        New-Item -ItemType Directory -Force -Path $testsRoot | Out-Null
        [IO.File]::WriteAllText((Join-Path $testsRoot "OrderTests.received.json"), "{`"id`":1}", [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $testsRoot "sensitive.golden"), "password=fixture-test-secret-1234567890", [Text.UTF8Encoding]::new($false))
        $blockingReportPath = Join-Path $workRoot "blocking-report.json"
        $blockingCode = Invoke-CommandCapture $fixtureVault @("scan", "--format", "json") $blockingReportPath
        Assert-Contract ($blockingCode -eq 1) "Blocking fixturevault scan returned $blockingCode instead of 1."
        $report = [IO.File]::ReadAllText($blockingReportPath) | ConvertFrom-Json
        Assert-Contract ($report.schemaVersion -eq 1) "Blocking scan JSON did not report schema version 1."
        Assert-Contract (@($report.findings | Where-Object { $_.ruleId -eq "FV001" }).Count -gt 0) "Blocking scan JSON did not contain the expected FV001 finding."
        Assert-Contract (@($report.findings | Where-Object { $_.ruleId -eq "FV007" }).Count -gt 0) "Blocking scan JSON did not contain the expected FV007 sensitive-data finding."
        Assert-Contract (-not ([IO.File]::ReadAllText($blockingReportPath).Contains("fixture-test-secret-1234567890", [StringComparison]::Ordinal))) "Sensitive data was disclosed by the package consumer report."

        $connectionPositiveRoot = Join-Path $workRoot "connection-positive"
        $connectionPositiveTestsRoot = Join-Path $connectionPositiveRoot "tests"
        New-Item -ItemType Directory -Force -Path $connectionPositiveTestsRoot | Out-Null
        Push-Location $connectionPositiveRoot
        try {
            [IO.File]::WriteAllText(
                (Join-Path $connectionPositiveTestsRoot "connection.golden"),
                ('Server=example.invalid;Password=' + '"""Canary123""";' + [Environment]::NewLine + "Server=example.invalid;Pwd='''Canary123''';" + [Environment]::NewLine + 'Server=example.invalid;Password=\"Canary123\";' + [Environment]::NewLine),
                [Text.UTF8Encoding]::new($false))
            Assert-Contract ((Invoke-CommandCapture $fixtureVault @("init") (Join-Path $workRoot "connection-positive-init.txt")) -eq 0) "Positive connection-string init failed."

            $positiveConsolePath = Join-Path $workRoot "connection-positive-console.txt"
            $positiveConsoleCode = Invoke-CommandCapture $fixtureVault @("scan") $positiveConsolePath
            Assert-Contract ($positiveConsoleCode -eq 1) "Positive connection-string console scan returned $positiveConsoleCode instead of 1."
            $positiveConsole = [IO.File]::ReadAllText($positiveConsolePath)
            Assert-Contract ($positiveConsole.Contains("FV007", [StringComparison]::Ordinal)) "Positive connection-string console scan did not report FV007."
            Assert-Contract (-not $positiveConsole.Contains("Canary123", [StringComparison]::Ordinal)) "Positive connection-string console scan disclosed the credential."

            $positiveJsonPath = Join-Path $workRoot "connection-positive.json"
            $positiveJsonCode = Invoke-CommandCapture $fixtureVault @("scan", "--format", "json") $positiveJsonPath
            Assert-Contract ($positiveJsonCode -eq 1) "Positive connection-string JSON scan returned $positiveJsonCode instead of 1."
            $positiveReportText = [IO.File]::ReadAllText($positiveJsonPath)
            $positiveReport = $positiveReportText | ConvertFrom-Json
            Assert-Contract (@($positiveReport.findings | Where-Object { $_.ruleId -eq "FV007" }).Count -gt 0) "Positive connection-string JSON scan did not report FV007."
            Assert-Contract (-not $positiveReportText.Contains("Canary123", [StringComparison]::Ordinal)) "Positive connection-string JSON scan disclosed the credential."
            Write-Host "Connection-string positive package smoke: console exit 1, JSON exit 1, including JSON-escaped quotes, FV007 present, credential undisclosed."
        }
        finally {
            Pop-Location
        }

        $connectionNegativeRoot = Join-Path $workRoot "connection-negative"
        $connectionNegativeTestsRoot = Join-Path $connectionNegativeRoot "tests"
        New-Item -ItemType Directory -Force -Path $connectionNegativeTestsRoot | Out-Null
        Push-Location $connectionNegativeRoot
        try {
            [IO.File]::WriteAllText(
                (Join-Path $connectionNegativeTestsRoot "empty.json.golden"),
                '{"ConnectionString":"Server=localhost;Password="}',
                [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText(
                (Join-Path $connectionNegativeTestsRoot "whitespace.json.golden"),
                '{"ConnectionString":"Server=localhost;Pwd=   "}',
                [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText(
                (Join-Path $connectionNegativeTestsRoot "redacted.json.golden"),
                '{"ConnectionString":"Server=localhost;Password=[redacted]"}',
                [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText(
                (Join-Path $connectionNegativeTestsRoot "escaped-empty.json.golden"),
                '{"ConnectionString":"Server=localhost;Password=\"\""}',
                [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText(
                (Join-Path $connectionNegativeTestsRoot "escaped-redacted.json.golden"),
                '{"ConnectionString":"Server=localhost;Password=\"[redacted]\""}',
                [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText(
                (Join-Path $connectionNegativeTestsRoot "escaped-pwd-empty.json.golden"),
                '{"ConnectionString":"Server=localhost;PWD=\"\""}',
                [Text.UTF8Encoding]::new($false))
            Assert-Contract ((Invoke-CommandCapture $fixtureVault @("init") (Join-Path $workRoot "connection-negative-init.txt")) -eq 0) "Negative connection-string init failed."

            $negativeConsolePath = Join-Path $workRoot "connection-negative-console.txt"
            $negativeConsoleCode = Invoke-CommandCapture $fixtureVault @("scan") $negativeConsolePath
            Assert-Contract ($negativeConsoleCode -eq 0) "Negative connection-string console scan returned $negativeConsoleCode instead of 0."
            $negativeConsole = [IO.File]::ReadAllText($negativeConsolePath)
            Assert-Contract (-not $negativeConsole.Contains("FV007", [StringComparison]::Ordinal)) "Negative connection-string console scan reported FV007."

            $negativeJsonPath = Join-Path $workRoot "connection-negative.json"
            $negativeJsonCode = Invoke-CommandCapture $fixtureVault @("scan", "--format", "json") $negativeJsonPath
            Assert-Contract ($negativeJsonCode -eq 0) "Negative connection-string JSON scan returned $negativeJsonCode instead of 0."
            $negativeReport = [IO.File]::ReadAllText($negativeJsonPath) | ConvertFrom-Json
            Assert-Contract (@($negativeReport.findings).Count -eq 0) "Negative connection-string JSON scan reported findings."
            Assert-Contract (@($negativeReport.errors).Count -eq 0) "Negative connection-string JSON scan reported errors."
            Write-Host "Connection-string negative package smoke: console exit 0, JSON exit 0, no findings for empty/whitespace/redacted and JSON-escaped quoted values."
        }
        finally {
            Pop-Location
        }

        if ([OperatingSystem]::IsLinux()) {
            $safetyRoot = Join-Path $workRoot "safety-consumer"
            $safetyTestsRoot = Join-Path $safetyRoot "tests"
            New-Item -ItemType Directory -Force -Path $safetyTestsRoot | Out-Null
            Push-Location $safetyRoot
            try {
                Assert-Contract ((Invoke-CommandCapture $fixtureVault @("init") (Join-Path $workRoot "safety-init.txt")) -eq 0) "Linux safety consumer init failed."

                $outsideRoot = Join-Path $workRoot "outside"
                New-Item -ItemType Directory -Force -Path $outsideRoot | Out-Null
                [IO.File]::WriteAllText((Join-Path $outsideRoot "outside.received.json"), "outside", [Text.UTF8Encoding]::new($false))
                $linkedDirectory = Join-Path $safetyTestsRoot "linked"
                New-Item -ItemType SymbolicLink -Path $linkedDirectory -Target $outsideRoot | Out-Null
                $linkReportPath = Join-Path $workRoot "link-report.json"
                $linkCode = Invoke-CommandCapture $fixtureVault @("scan", "--format", "json") $linkReportPath
                Assert-Contract ($linkCode -eq 0) "Linux symlink scan returned $linkCode instead of 0."
                $linkReport = [IO.File]::ReadAllText($linkReportPath) | ConvertFrom-Json
                Assert-Contract (@($linkReport.skipped | Where-Object { $_.code -eq "FV-SKIP-REPARSE" }).Count -gt 0) "Linux symlink scan did not report FV-SKIP-REPARSE."
                Assert-Contract (-not ([IO.File]::ReadAllText($linkReportPath).Contains("outside.received.json", [StringComparison]::Ordinal))) "Linux symlink scan disclosed an outside filename."
                Remove-Item -LiteralPath $linkedDirectory -Force

                $fifoPath = Join-Path $safetyTestsRoot "blocking.golden"
                & mkfifo $fifoPath
                Assert-Contract ($LASTEXITCODE -eq 0) "Linux FIFO could not be created for the package consumer test."
                $fifoReportPath = Join-Path $workRoot "fifo-report.json"
                $fifoCode = Invoke-CommandCapture $fixtureVault @("scan", "--format", "json") $fifoReportPath
                Assert-Contract ($fifoCode -eq 2) "Linux FIFO scan returned $fifoCode instead of 2."
                $fifoReport = [IO.File]::ReadAllText($fifoReportPath) | ConvertFrom-Json
                Assert-Contract (@($fifoReport.errors | Where-Object { $_.code -eq "FV-E009" }).Count -gt 0) "Linux FIFO scan did not report FV-E009."
            }
            finally {
                Pop-Location
            }
        }
    }
    finally {
        Pop-Location
    }

    Write-Host "Consumer smoke passed: help, init, clean scan (0), sensitive/blocking JSON scan (1), connection-string positive/negative console+JSON cases, and Linux filesystem safety checks when applicable."
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
