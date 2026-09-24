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
            (Join-Path $consumerRoot "asset.png"),
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
        Assert-Contract ($help -match "Usage:" -and $help -match "fixturevault" -and $help -match "api_key/api-key" -and $help -match "client_secret/client-secret" -and $help -match "must be unquoted or use matching single/double quotes" -and $help -match "mismatched quotes are not assignment syntax" -and $help -match "Both '=' and ':' are supported" -and $help -match "JSON credential properties classify string, number, true, and false scalars" -and $help -match "Null and empty/whitespace strings are clean" -and $help -match "Parsed generic fields own only their exact key/operator/value spans" -and $help -match "Clean fields never suppress later fields or JSON siblings" -and $help -match "the shared sibling grammar recognizes '=' and ':' forms" -and $help -match "URI-like values stay intact" -and $help -match "only Azure credential keys are classified" -and $help -match "Raw connection-string Password/Pwd values preserve backslash spellings literally" -and $help -match "doubled-quote runs" -and $help -match "u0022") "fixturevault --help did not print the expected usage text."

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
        New-Item -ItemType Directory -Force -Path $connectionPositiveRoot | Out-Null
        $connectionPositiveCases = @(
            [pscustomobject]@{ Name = "raw-doubled-double"; Fixture = 'Server=example.invalid;Password="""Canary123""";'; Secret = "Canary123" },
            [pscustomobject]@{ Name = "raw-doubled-single"; Fixture = "Server=example.invalid;Pwd='''Canary123''';"; Secret = "Canary123" },
            [pscustomobject]@{ Name = "raw-literal-unicode-spelling"; Fixture = 'Server=example.invalid;Pwd=\u0022UnicodeCanary123\u0022;'; Secret = "UnicodeCanary123" },
            [pscustomobject]@{ Name = "json-doubled-double"; Fixture = ('Server=localhost;Password="""JsonDoubledCanary123""";' | ConvertTo-Json -Compress); Secret = "JsonDoubledCanary123" },
            [pscustomobject]@{ Name = "json-literal-unicode-spelling"; Fixture = ('Server=localhost;Pwd=\u0022JsonUnicodeCanary123\u0022;' | ConvertTo-Json -Compress); Secret = "JsonUnicodeCanary123" },
            [pscustomobject]@{ Name = "raw-quote-data"; Fixture = 'Server=localhost;Pwd=''""'';'; Secret = "" }
        )

        foreach ($case in $connectionPositiveCases) {
            $caseRoot = Join-Path $connectionPositiveRoot $case.Name
            $caseTestsRoot = Join-Path $caseRoot "tests"
            New-Item -ItemType Directory -Force -Path $caseTestsRoot | Out-Null
            $fixturePath = Join-Path $caseTestsRoot "$($case.Name).golden"
            [IO.File]::WriteAllText($fixturePath, $case.Fixture + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
            $beforeHash = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash

            Push-Location $caseRoot
            try {
                Assert-Contract ((Invoke-CommandCapture $fixtureVault @("init") (Join-Path $workRoot "$($case.Name)-init.txt")) -eq 0) "$($case.Name) init failed."

                $consolePath = Join-Path $workRoot "$($case.Name)-console.txt"
                $consoleCode = Invoke-CommandCapture $fixtureVault @("scan") $consolePath
                Assert-Contract ($consoleCode -eq 1) "$($case.Name) console scan returned $consoleCode instead of 1."
                $consoleText = [IO.File]::ReadAllText($consolePath)
                Assert-Contract ($consoleText.Contains("FV007", [StringComparison]::Ordinal)) "$($case.Name) console scan did not report FV007."
                if ($case.Secret.Length -gt 0) {
                    Assert-Contract (-not $consoleText.Contains($case.Secret, [StringComparison]::Ordinal)) "$($case.Name) console output disclosed the credential."
                }

                $jsonPath = Join-Path $workRoot "$($case.Name)-json.txt"
                $jsonCode = Invoke-CommandCapture $fixtureVault @("scan", "--format", "json") $jsonPath
                Assert-Contract ($jsonCode -eq 1) "$($case.Name) JSON scan returned $jsonCode instead of 1."
                $jsonText = [IO.File]::ReadAllText($jsonPath)
                $jsonReport = $jsonText | ConvertFrom-Json
                Assert-Contract ($jsonReport.filesInspected -eq 1) "$($case.Name) JSON scan did not inspect exactly the intended fixture."
                Assert-Contract (@($jsonReport.errors).Count -eq 0) "$($case.Name) JSON scan reported an execution error."
                $jsonFindings = @($jsonReport.findings | Where-Object { $_.ruleId -eq "FV007" })
                Assert-Contract ($jsonFindings.Count -eq 1 -and $jsonFindings[0].path -eq "tests/$($case.Name).golden") "$($case.Name) JSON scan did not report exactly one FV007 for the intended fixture path."
                if ($case.Secret.Length -gt 0) {
                    Assert-Contract (-not $jsonText.Contains($case.Secret, [StringComparison]::Ordinal)) "$($case.Name) JSON output disclosed the credential."
                }

                $afterHash = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash
                Assert-Contract ($beforeHash -eq $afterHash) "$($case.Name) scan mutated the fixture."
                Write-Host "$($case.Name) package smoke: isolated console exit 1 and JSON exit 1, exactly one FV007, no execution error, no disclosure, no mutation."
            }
            finally {
                Pop-Location
            }
        }

        $structuredCases = @(
            [pscustomobject]@{ Name = "azure-positive"; Fixture = 'AccountKey=fixture-azure-package-secret-1234567890;'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "fixture-azure-package-secret-1234567890" },
            [pscustomobject]@{ Name = "raw-api-key-alias"; Fixture = 'api_key=fixture-api-key-package-secret-1234567890'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "fixture-api-key-package-secret-1234567890" },
            [pscustomobject]@{ Name = "raw-client-secret-alias"; Fixture = 'client_secret=fixture-client-package-secret-1234567890'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "fixture-client-package-secret-1234567890" },
            [pscustomobject]@{ Name = "raw-matched-double-quoted-key"; Fixture = '"api_key"=fixture-double-quoted-key-package-secret-1234567890'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "fixture-double-quoted-key-package-secret-1234567890" },
            [pscustomobject]@{ Name = "raw-matched-single-quoted-key"; Fixture = "'client_secret'=fixture-single-quoted-key-package-secret-1234567890"; ExpectedExit = 1; ExpectedFinding = $true; Secret = "fixture-single-quoted-key-package-secret-1234567890" },
            [pscustomobject]@{ Name = "raw-leading-quote-only-key"; Fixture = '"api_key=fixture-leading-quote-package-secret-1234567890'; ExpectedExit = 0; ExpectedFinding = $false; Secret = "fixture-leading-quote-package-secret-1234567890" },
            [pscustomobject]@{ Name = "raw-trailing-quote-only-key"; Fixture = 'api_key"=fixture-trailing-quote-package-secret-1234567890'; ExpectedExit = 0; ExpectedFinding = $false; Secret = "fixture-trailing-quote-package-secret-1234567890" },
            [pscustomobject]@{ Name = "raw-mismatched-double-single-key"; Fixture = '"api_key''=fixture-mismatched-double-single-package-secret-1234567890'; ExpectedExit = 0; ExpectedFinding = $false; Secret = "fixture-mismatched-double-single-package-secret-1234567890" },
            [pscustomobject]@{ Name = "raw-mismatched-single-double-key"; Fixture = '''api_key"=fixture-mismatched-single-double-package-secret-1234567890'; ExpectedExit = 0; ExpectedFinding = $false; Secret = "fixture-mismatched-single-double-package-secret-1234567890" },
            [pscustomobject]@{ Name = "json-api-key-property"; Fixture = '{"api_key":"fixture-json-property-package-secret-1234567890"}'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "fixture-json-property-package-secret-1234567890" },
            [pscustomobject]@{ Name = "json-later-secret"; Fixture = '{"metadata":"kind=fixture","payload":"token=fixture-json-sibling-package-secret-1234567890"}'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "fixture-json-sibling-package-secret-1234567890" },
            [pscustomobject]@{ Name = "raw-benign-before-colon"; Fixture = 'kind=fixture; token: fixture-colon-package-secret-1234567890'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "fixture-colon-package-secret-1234567890" },
            [pscustomobject]@{ Name = "raw-comma-benign-before-colon"; Fixture = 'kind=fixture, token: fixture-comma-package-secret-1234567890'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "fixture-comma-package-secret-1234567890" },
            [pscustomobject]@{ Name = "raw-space-benign-before-colon"; Fixture = 'kind=fixture token: fixture-space-package-secret-1234567890'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "fixture-space-package-secret-1234567890" },
            [pscustomobject]@{ Name = "raw-prefix-before-colon"; Fixture = 'metadata token: fixture-prefix-package-secret-1234567890'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "fixture-prefix-package-secret-1234567890" },
            [pscustomobject]@{ Name = "raw-adjacent-punctuation"; Fixture = 'metadata|api_key=fixture-punctuation-package-secret-1234567890'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "fixture-punctuation-package-secret-1234567890" },
            [pscustomobject]@{ Name = "json-nested-comma-hidden"; Fixture = '{"outer":[{"payload":"kind=fixture, token: fixture-nested-package-secret-1234567890"}]}'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "fixture-nested-package-secret-1234567890" },
            [pscustomobject]@{ Name = "json-array-prefix-hidden"; Fixture = '["metadata token: fixture-array-package-secret-1234567890","kind=fixture"]'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "fixture-array-package-secret-1234567890" },
            [pscustomobject]@{ Name = "raw-marker-comma-benign"; Fixture = 'token: [redacted], kind=fixture'; ExpectedExit = 0; ExpectedFinding = $false; Secret = "" },
            [pscustomobject]@{ Name = "raw-benign-comma-marker"; Fixture = 'kind=fixture, token: [redacted]'; ExpectedExit = 0; ExpectedFinding = $false; Secret = "" },
            [pscustomobject]@{ Name = "raw-marker-space-benign"; Fixture = 'token: [redacted] kind=fixture'; ExpectedExit = 0; ExpectedFinding = $false; Secret = "" },
            [pscustomobject]@{ Name = "raw-benign-space-marker"; Fixture = 'kind=fixture token: [redacted]'; ExpectedExit = 0; ExpectedFinding = $false; Secret = "" },
            [pscustomobject]@{ Name = "raw-marker-comma-secret"; Fixture = 'token: [redacted], api_key=fixture-marker-sibling-package-secret-1234567890'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "fixture-marker-sibling-package-secret-1234567890" },
            [pscustomobject]@{ Name = "azure-all-marker-siblings"; Fixture = 'AccountKey=***,SharedAccessKey=[redacted]'; ExpectedExit = 0; ExpectedFinding = $false; Secret = "" },
            [pscustomobject]@{ Name = "azure-marker-real-sibling"; Fixture = 'AccountKey=***,SharedAccessKey=fixture-azure-sibling-package-secret-1234567890'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "fixture-azure-sibling-package-secret-1234567890" },
            [pscustomobject]@{ Name = "azure-empty-harmless-sibling"; Fixture = 'AccountKey= EndpointSuffix=core.windows.net'; ExpectedExit = 0; ExpectedFinding = $false; Secret = "" },
            [pscustomobject]@{ Name = "azure-harmless-sibling-empty"; Fixture = 'EndpointSuffix=core.windows.net AccountKey='; ExpectedExit = 0; ExpectedFinding = $false; Secret = "" },
            [pscustomobject]@{ Name = "azure-empty-real-sibling"; Fixture = 'AccountKey= SharedAccessKey=fixture-azure-empty-real-package-secret-1234567890'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "fixture-azure-empty-real-package-secret-1234567890" },
            [pscustomobject]@{ Name = "azure-harmless-sibling-real"; Fixture = 'EndpointSuffix=core.windows.net AccountKey=fixture-azure-harmless-real-package-secret-1234567890'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "fixture-azure-harmless-real-package-secret-1234567890" },
            [pscustomobject]@{ Name = "azure-empty-unknown-colon"; Fixture = 'AccountKey= note: [redacted]'; ExpectedExit = 0; ExpectedFinding = $false; Secret = "" },
            [pscustomobject]@{ Name = "azure-uri-like-value"; Fixture = 'AccountKey= https://example.invalid/fixture'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "https://example.invalid/fixture" },
            [pscustomobject]@{ Name = "api-header-literal-escape"; Fixture = 'X-Api-Key: \u0022\u0022'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "" },
            [pscustomobject]@{ Name = "api-query-literal-escape"; Fixture = 'https://example.invalid/?api_key=%5Cu0022%5Cu0022'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "" },
            [pscustomobject]@{ Name = "basic-marker"; Fixture = 'Authorization: Basic redacted'; ExpectedExit = 0; ExpectedFinding = $false; Secret = "" },
            [pscustomobject]@{ Name = "cookie-sibling-positive"; Fixture = 'Cookie: empty=; second=fixture-cookie-package-secret-1234567890'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "fixture-cookie-package-secret-1234567890" },
            [pscustomobject]@{ Name = "generic-marker"; Fixture = 'password=<redacted>'; ExpectedExit = 0; ExpectedFinding = $false; Secret = "" }
        )

        $jsonCredentialKeys = @(
            "ApiKey",
            "apiKey",
            "APIKEY",
            "api_key",
            "api-key",
            "ClientSecret",
            "client_secret",
            "client-secret",
            "Password",
            "Pwd",
            "Secret",
            "Token"
        )
        $jsonCredentialMarkers = @("***", "<redacted>", "[redacted]", "redacted", "masked", "removed")
        $jsonStringSecret = "fixture-json-scalar-package-secret-1234567890"
        $jsonNumberSecret = "1234567890123456789"

        for ($keyIndex = 0; $keyIndex -lt $jsonCredentialKeys.Count; $keyIndex++) {
            $keyJson = ConvertTo-Json -InputObject $jsonCredentialKeys[$keyIndex] -Compress
            $structuredCases += [pscustomobject]@{
                Name = "json-scalar-$($keyIndex.ToString('D2'))-00"
                Fixture = '{{{0}:{1}}}' -f $keyJson, (ConvertTo-Json -InputObject $jsonStringSecret -Compress)
                ExpectedExit = 1
                ExpectedFinding = $true
                Secret = $jsonStringSecret
            }
            $structuredCases += [pscustomobject]@{
                Name = "json-scalar-$($keyIndex.ToString('D2'))-01"
                Fixture = '{{{0}:{1}}}' -f $keyJson, $jsonNumberSecret
                ExpectedExit = 1
                ExpectedFinding = $true
                Secret = $jsonNumberSecret
            }
            $structuredCases += [pscustomobject]@{
                Name = "json-scalar-$($keyIndex.ToString('D2'))-02"
                Fixture = '{{{0}:true}}' -f $keyJson
                ExpectedExit = 1
                ExpectedFinding = $true
                Secret = "true"
            }
            $structuredCases += [pscustomobject]@{
                Name = "json-scalar-$($keyIndex.ToString('D2'))-03"
                Fixture = '{{{0}:false}}' -f $keyJson
                ExpectedExit = 1
                ExpectedFinding = $true
                Secret = "false"
            }

            for ($markerIndex = 0; $markerIndex -lt $jsonCredentialMarkers.Count; $markerIndex++) {
                $marker = $jsonCredentialMarkers[$markerIndex]
                $structuredCases += [pscustomobject]@{
                    Name = "json-marker-$($keyIndex.ToString('D2'))-$($markerIndex.ToString('D2'))"
                    Fixture = '{{{0}:{1}}}' -f $keyJson, (ConvertTo-Json -InputObject $marker -Compress)
                    ExpectedExit = 0
                    ExpectedFinding = $false
                    Secret = $marker
                }
            }

            $structuredCases += [pscustomobject]@{
                Name = "json-clean-$($keyIndex.ToString('D2'))-00"
                Fixture = '{' + $keyJson + ':""}'
                ExpectedExit = 0
                ExpectedFinding = $false
                Secret = ""
            }
            $structuredCases += [pscustomobject]@{
                Name = "json-clean-$($keyIndex.ToString('D2'))-01"
                Fixture = '{' + $keyJson + ':" \t "}'
                ExpectedExit = 0
                ExpectedFinding = $false
                Secret = ""
            }
            $structuredCases += [pscustomobject]@{
                Name = "json-clean-$($keyIndex.ToString('D2'))-02"
                Fixture = '{' + $keyJson + ':null}'
                ExpectedExit = 0
                ExpectedFinding = $false
                Secret = ""
            }
            $structuredCases += [pscustomobject]@{
                Name = "json-container-$($keyIndex.ToString('D2'))-00"
                Fixture = '{' + $keyJson + ':{"metadata":"kind=fixture"}}'
                ExpectedExit = 0
                ExpectedFinding = $false
                Secret = ""
            }
            $structuredCases += [pscustomobject]@{
                Name = "json-container-$($keyIndex.ToString('D2'))-01"
                Fixture = '{' + $keyJson + ':["kind=fixture"]}'
                ExpectedExit = 0
                ExpectedFinding = $false
                Secret = ""
            }
        }

        $structuredCases += @(
            [pscustomobject]@{ Name = "json-nested-scalar-00"; Fixture = '{"outer":{"api_key":1234567890123456789}}'; ExpectedExit = 1; ExpectedFinding = $true; Secret = $jsonNumberSecret },
            [pscustomobject]@{ Name = "json-nested-scalar-01"; Fixture = '[{"metadata":"kind=fixture"},{"Token":false}]'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "false" },
            [pscustomobject]@{ Name = "json-order-scalar-00"; Fixture = '{"metadata":"kind=fixture","Token":true}'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "true" },
            [pscustomobject]@{ Name = "json-order-scalar-01"; Fixture = '{"Token":true,"metadata":"kind=fixture"}'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "true" },
            [pscustomobject]@{ Name = "json-container-nested-00"; Fixture = '{"Token":{"client_secret":1234567890123456789}}'; ExpectedExit = 1; ExpectedFinding = $true; Secret = $jsonNumberSecret },
            [pscustomobject]@{ Name = "json-container-nested-01"; Fixture = '{"Token":[{"api-key":false}]}'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "false" }
        )

        $azureColonKeys = @("AccountKey", "SharedAccessKey", "SharedAccessSignature")
        $azureColonSiblings = @(
            [pscustomobject]@{ Name = "marker"; Assignment = 'token: [redacted]'; ExpectedExit = 0; ExpectedFinding = $false; Secret = "" },
            [pscustomobject]@{ Name = "real"; Assignment = 'token: fixture-generic-colon-package-secret-1234567890'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "fixture-generic-colon-package-secret-1234567890" }
        )

        foreach ($azureKey in $azureColonKeys) {
            foreach ($sibling in $azureColonSiblings) {
                foreach ($azureFirst in @($true, $false)) {
                    $sequence = if ($azureFirst) {
                        "$azureKey= $($sibling.Assignment)"
                    }
                    else {
                        "$($sibling.Assignment) $azureKey="
                    }
                    $order = if ($azureFirst) { "azure-first" } else { "generic-first" }
                    $keyName = $azureKey.ToLowerInvariant()
                    $representations = @(
                        [pscustomobject]@{ Name = "raw"; Fixture = $sequence },
                        [pscustomobject]@{
                            Name = "nested-json"
                            Fixture = ConvertTo-Json -InputObject ([pscustomobject]@{
                                outer = [pscustomobject]@{ payload = $sequence }
                            }) -Compress -Depth 5
                        },
                        [pscustomobject]@{
                            Name = "json-array"
                            Fixture = ConvertTo-Json -InputObject @("kind=fixture", $sequence) -Compress
                        }
                    )

                    foreach ($representation in $representations) {
                        $structuredCases += [pscustomobject]@{
                            Name = "azure-colon-$keyName-$($sibling.Name)-$order-$($representation.Name)"
                            Fixture = $representation.Fixture
                            ExpectedExit = $sibling.ExpectedExit
                            ExpectedFinding = $sibling.ExpectedFinding
                            Secret = $sibling.Secret
                        }
                    }
                }
            }
        }

        foreach ($case in $structuredCases) {
            $caseRoot = Join-Path $connectionPositiveRoot "structured-$($case.Name)"
            $caseTestsRoot = Join-Path $caseRoot "tests"
            New-Item -ItemType Directory -Force -Path $caseTestsRoot | Out-Null
            $fixturePath = Join-Path $caseTestsRoot "$($case.Name).golden"
            [IO.File]::WriteAllText($fixturePath, $case.Fixture + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
            $beforeHash = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash

            Push-Location $caseRoot
            try {
                Assert-Contract ((Invoke-CommandCapture $fixtureVault @("init") (Join-Path $workRoot "structured-$($case.Name)-init.txt")) -eq 0) "structured $($case.Name) init failed."

                $consolePath = Join-Path $workRoot "structured-$($case.Name)-console.txt"
                $consoleCode = Invoke-CommandCapture $fixtureVault @("scan") $consolePath
                Assert-Contract ($consoleCode -eq $case.ExpectedExit) "structured $($case.Name) console scan returned $consoleCode instead of $($case.ExpectedExit)."
                $consoleText = [IO.File]::ReadAllText($consolePath)
                Assert-Contract (($consoleText.Contains("FV007", [StringComparison]::Ordinal)) -eq $case.ExpectedFinding) "structured $($case.Name) console finding classification was incorrect."
                Assert-Contract (-not $consoleText.Contains($case.Fixture, [StringComparison]::Ordinal)) "structured $($case.Name) console output disclosed the fixture representation."
                $expectedFixturePath = "tests/$($case.Name).golden"
                $consoleFindingCount = [regex]::Matches($consoleText, '(?m)^FV007 ').Count
                Assert-Contract ($consoleFindingCount -eq [int]$case.ExpectedFinding) "structured $($case.Name) console scan did not report the exact FV007 finding count."
                if ($case.ExpectedFinding) {
                    Assert-Contract ($consoleText.Contains("FV007 block $expectedFixturePath", [StringComparison]::Ordinal)) "structured $($case.Name) console scan did not identify the intended fixture path."
                    Assert-Contract ($consoleText.Contains("1 policy-blocking finding(s).", [StringComparison]::Ordinal)) "structured $($case.Name) console summary did not report exactly one blocking finding."
                }

                $jsonPath = Join-Path $workRoot "structured-$($case.Name)-json.txt"
                $jsonCode = Invoke-CommandCapture $fixtureVault @("scan", "--format", "json") $jsonPath
                Assert-Contract ($jsonCode -eq $case.ExpectedExit) "structured $($case.Name) JSON scan returned $jsonCode instead of $($case.ExpectedExit)."
                $jsonText = [IO.File]::ReadAllText($jsonPath)
                Assert-Contract (-not $jsonText.Contains($case.Fixture, [StringComparison]::Ordinal)) "structured $($case.Name) JSON output disclosed the fixture representation."
                $jsonReport = $jsonText | ConvertFrom-Json
                Assert-Contract ($jsonReport.filesInspected -eq 1 -and @($jsonReport.errors).Count -eq 0) "structured $($case.Name) JSON scan did not inspect one valid fixture without errors."
                $jsonFindings = @($jsonReport.findings | Where-Object { $_.ruleId -eq "FV007" })
                Assert-Contract (@($jsonReport.findings).Count -eq [int]$case.ExpectedFinding) "structured $($case.Name) JSON scan did not report the exact total finding count."
                Assert-Contract (($jsonFindings.Count -eq 1 -and $jsonFindings[0].path -eq $expectedFixturePath) -eq $case.ExpectedFinding) "structured $($case.Name) JSON finding classification or path was incorrect."
                if ($case.Secret.Length -gt 0) {
                    Assert-Contract (-not $consoleText.Contains($case.Secret, [StringComparison]::Ordinal) -and -not $jsonText.Contains($case.Secret, [StringComparison]::Ordinal)) "structured $($case.Name) disclosed its credential."
                }

                $afterHash = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash
                Assert-Contract ($beforeHash -eq $afterHash) "structured $($case.Name) scan mutated the fixture."
                Write-Host "structured $($case.Name) package smoke: console/JSON classification $($case.ExpectedFinding), no execution error, no disclosure, no mutation."
            }
            finally {
                Pop-Location
            }
        }

        $strictRoot = Join-Path $connectionPositiveRoot "strict-override"
        $strictTestsRoot = Join-Path $strictRoot "tests"
        New-Item -ItemType Directory -Force -Path $strictTestsRoot | Out-Null
        [IO.File]::WriteAllText((Join-Path $strictTestsRoot "strict.golden"), "Server=localhost;Pwd=strict-package-secret-1234567890;" + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        Push-Location $strictRoot
        try {
            Assert-Contract ((Invoke-CommandCapture $fixtureVault @("init") (Join-Path $workRoot "strict-override-init.txt")) -eq 0) "Strict override init failed."
            $policyPath = Join-Path $strictRoot ".fixturevault.json"
            $policy = [IO.File]::ReadAllText($policyPath) | ConvertFrom-Json
            $policy.ci.strict = $false
            [IO.File]::WriteAllText($policyPath, ($policy | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))

            $warningPath = Join-Path $workRoot "strict-override-warning.json"
            $warningCode = Invoke-CommandCapture $fixtureVault @("scan", "--format", "json") $warningPath
            $warningReport = [IO.File]::ReadAllText($warningPath) | ConvertFrom-Json
            Assert-Contract ($warningCode -eq 0 -and @($warningReport.findings | Where-Object { $_.ruleId -eq "FV007" -and $_.disposition -eq "warn" }).Count -eq 1) "Non-strict package scan did not return exit 0 with a warning FV007."

            $strictPath = Join-Path $workRoot "strict-override-block.json"
            $strictCode = Invoke-CommandCapture $fixtureVault @("scan", "--format", "json", "--strict") $strictPath
            $strictReport = [IO.File]::ReadAllText($strictPath) | ConvertFrom-Json
            Assert-Contract ($strictCode -eq 1 -and @($strictReport.findings | Where-Object { $_.ruleId -eq "FV007" -and $_.disposition -eq "block" }).Count -eq 1) "--strict did not override the package policy to a blocking FV007."
            Write-Host 'Strict override package smoke: non-strict exit 0/warn and --strict exit 1/block.'
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
                (Join-Path $connectionNegativeTestsRoot "json-doubled-empty.json.golden"),
                '{"ConnectionString":"Server=localhost;Password=\"\""}',
                [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText(
                (Join-Path $connectionNegativeTestsRoot "json-doubled-redacted.json.golden"),
                '{"ConnectionString":"Server=localhost;Password=\"\"\"[redacted]\"\"\""}',
                [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText(
                (Join-Path $connectionNegativeTestsRoot "escaped-pwd-empty.json.golden"),
                '{"ConnectionString":"Server=localhost;PWD=\"\""}',
                [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText(
                (Join-Path $connectionNegativeTestsRoot "unicode-empty.json.golden"),
                '{"ConnectionString":"Server=localhost;Password=\u0022\u0022"}',
                [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText(
                (Join-Path $connectionNegativeTestsRoot "unicode-redacted.json.golden"),
                '{"ConnectionString":"Server=localhost;Password=\u0022[redacted]\u0022"}',
                [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText(
                (Join-Path $connectionNegativeTestsRoot "unicode-whitespace.json.golden"),
                '{"ConnectionString":"Server=localhost;Pwd=\u0022\u0009\u0022"}',
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
            Write-Host 'Connection-string negative package smoke: console exit 0, JSON exit 0, no findings for empty/whitespace/redacted and JSON-wrapped doubled-quote values.'
        }
        finally {
            Pop-Location
        }

        $connectionContextRoot = Join-Path $workRoot "connection-context"
        New-Item -ItemType Directory -Force -Path $connectionContextRoot | Out-Null
        $connectionContextCases = @(
            [pscustomobject]@{ Name = "raw-unicode-quote-empty"; Fixture = 'Server=example.invalid;Password=\u0022\u0022;'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "" },
            [pscustomobject]@{ Name = "json-unicode-quote-empty"; Fixture = '{"ConnectionString":"Server=localhost;Password=\u0022\u0022"}'; ExpectedExit = 0; ExpectedFinding = $false; Secret = "" },
            [pscustomobject]@{ Name = "raw-tab-escape"; Fixture = 'Server=example.invalid;Password=\t;'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "" },
            [pscustomobject]@{ Name = "json-quoted-tab"; Fixture = '{"ConnectionString":"Server=localhost;Password=\u0022\t\u0022"}'; ExpectedExit = 0; ExpectedFinding = $false; Secret = "" },
            [pscustomobject]@{ Name = "raw-unicode-redacted-looking"; Fixture = 'Server=example.invalid;Password=\u0022[redacted]\u0022;'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "" },
            [pscustomobject]@{ Name = "json-unicode-redacted"; Fixture = '{"ConnectionString":"Server=localhost;Password=\u0022[redacted]\u0022"}'; ExpectedExit = 0; ExpectedFinding = $false; Secret = "" },
            [pscustomobject]@{ Name = "json-genuine-credential"; Fixture = '{"ConnectionString":"Server=localhost;Password=\u0022package-context-secret\u0022"}'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "package-context-secret" },
            [pscustomobject]@{ Name = "json-double-encoded-raw-escape"; Fixture = '{"ConnectionString":"Server=localhost;Password=\\u0022\\u0022"}'; ExpectedExit = 1; ExpectedFinding = $true; Secret = "" }
        )

        foreach ($case in $connectionContextCases) {
            $caseRoot = Join-Path $connectionContextRoot $case.Name
            $caseTestsRoot = Join-Path $caseRoot "tests"
            New-Item -ItemType Directory -Force -Path $caseTestsRoot | Out-Null
            [IO.File]::WriteAllText(
                (Join-Path $caseTestsRoot "$($case.Name).golden"),
                $case.Fixture + [Environment]::NewLine,
                [Text.UTF8Encoding]::new($false))

            Push-Location $caseRoot
            try {
                Assert-Contract ((Invoke-CommandCapture $fixtureVault @("init") (Join-Path $workRoot "$($case.Name)-init.txt")) -eq 0) "$($case.Name) init failed."

                $contextConsolePath = Join-Path $workRoot "$($case.Name)-console.txt"
                $contextConsoleCode = Invoke-CommandCapture $fixtureVault @("scan") $contextConsolePath
                Assert-Contract ($contextConsoleCode -eq $case.ExpectedExit) "$($case.Name) console scan returned $contextConsoleCode instead of $($case.ExpectedExit)."
                $contextConsole = [IO.File]::ReadAllText($contextConsolePath)
                Assert-Contract ($contextConsole.Contains("FV007", [StringComparison]::Ordinal) -eq $case.ExpectedFinding) "$($case.Name) console finding classification was incorrect."

                $contextJsonPath = Join-Path $workRoot "$($case.Name)-json.txt"
                $contextJsonCode = Invoke-CommandCapture $fixtureVault @("scan", "--format", "json") $contextJsonPath
                Assert-Contract ($contextJsonCode -eq $case.ExpectedExit) "$($case.Name) JSON scan returned $contextJsonCode instead of $($case.ExpectedExit)."
                $contextJsonText = [IO.File]::ReadAllText($contextJsonPath)
                $contextReport = $contextJsonText | ConvertFrom-Json
                Assert-Contract (@($contextReport.errors).Count -eq 0) "$($case.Name) JSON scan reported an execution error."
                $contextFindings = @($contextReport.findings | Where-Object { $_.ruleId -eq "FV007" })
                Assert-Contract (($contextFindings.Count -eq 1 -and $contextFindings[0].path -eq "tests/$($case.Name).golden") -eq $case.ExpectedFinding) "$($case.Name) JSON finding classification or path was incorrect."
                if ($case.Secret.Length -gt 0) {
                    Assert-Contract (-not $contextConsole.Contains($case.Secret, [StringComparison]::Ordinal) -and -not $contextJsonText.Contains($case.Secret, [StringComparison]::Ordinal)) "$($case.Name) disclosed its credential."
                }

                Write-Host "$($case.Name) package smoke: console exit $contextConsoleCode, JSON exit $contextJsonCode, expected FV007=$($case.ExpectedFinding)."
            }
            finally {
                Pop-Location
            }
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
