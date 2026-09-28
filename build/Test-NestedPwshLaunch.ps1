[CmdletBinding()]
param(
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$helperPath = Join-Path $PSScriptRoot 'Invoke-NestedPwsh.ps1'
$guardPath = $PSCommandPath

function Get-LaunchViolations([string]$Path) {
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        return @("${Path} contains PowerShell parse errors.")
    }

    $violations = [System.Collections.Generic.List[string]]::new()
    $commands = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true)
    foreach ($command in $commands) {
        $nameAst = $command.CommandElements[0]
        $commandName = if ($nameAst -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
            $nameAst.Value
        }
        elseif ($nameAst -is [System.Management.Automation.Language.ExpandableStringExpressionAst]) {
            $nameAst.Value
        }
        else {
            $null
        }

        if ($commandName -match '^(?i:pwsh|powershell)(?:\.exe)?$') {
            [void]$violations.Add("${Path}:$($command.Extent.StartLineNumber): direct nested PowerShell launch")
            continue
        }

        $literalArguments = @($command.CommandElements | Select-Object -Skip 1 | Where-Object {
                $_ -is [System.Management.Automation.Language.StringConstantExpressionAst]
            } | ForEach-Object { $_.Value })
        if ($literalArguments | Where-Object { $_ -match '^(?i:pwsh|powershell)(?:\.exe)?$' }) {
            [void]$violations.Add("${Path}:$($command.Extent.StartLineNumber): nested PowerShell executable passed to '$commandName'")
        }

        if ($commandName -eq 'Start-Process' -and -not (Test-StartProcessContainment $command)) {
            [void]$violations.Add("${Path}:$($command.Extent.StartLineNumber): Start-Process lacks hidden-window containment")
        }
    }

    return $violations.ToArray()
}

function Test-StartProcessContainment([System.Management.Automation.Language.CommandAst]$Command) {
    $elements = @($Command.CommandElements | Select-Object -Skip 1)
    for ($index = 0; $index -lt $elements.Count; $index++) {
        $element = $elements[$index]
        if ($element -isnot [System.Management.Automation.Language.CommandParameterAst]) {
            continue
        }

        $parameterName = $element.ParameterName
        $attachedArgument = $null
        if ($parameterName.Contains('=')) {
            $parameterParts = $parameterName.Split('=', 2)
            $parameterName = $parameterParts[0]
            $attachedArgument = $parameterParts[1]
        }
        if ($parameterName -in @('WindowStyle', 'wi')) {
            if ($attachedArgument -ieq 'Hidden') {
                return $true
            }
            $argument = $element.Argument
            if ($null -eq $argument -and $index + 1 -lt $elements.Count -and
                $elements[$index + 1] -isnot [System.Management.Automation.Language.CommandParameterAst]) {
                $argument = $elements[$index + 1]
            }
            if ($argument -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
                $argument.Value -ieq 'Hidden') {
                return $true
            }
            if ($argument -is [System.Management.Automation.Language.ExpandableStringExpressionAst] -and
                $argument.NestedExpressions.Count -eq 0 -and
                $argument.Value -ieq 'Hidden') {
                return $true
            }
        }

        if ($parameterName -in @('NoNewWindow', 'nnw')) {
            if ($attachedArgument -ieq '$true') {
                return $true
            }
            if ($null -eq $element.Argument) {
                return $true
            }
            if ($element.Argument -is [System.Management.Automation.Language.VariableExpressionAst] -and
                $element.Argument.VariablePath.UserPath -ieq 'true') {
                return $true
            }
        }
    }

    return $false
}

if ($SelfTest) {
    $selfTestRoot = Join-Path ([IO.Path]::GetTempPath()) "nested-pwsh-guard-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $selfTestRoot -Force | Out-Null
    try {
        $directPath = Join-Path $selfTestRoot 'direct.ps1'
        $processPath = Join-Path $selfTestRoot 'process.ps1'
        $safePath = Join-Path $selfTestRoot 'safe.ps1'
        $safeThenUnsafePath = Join-Path $selfTestRoot 'safe-then-unsafe.ps1'
        $unsafeThenSafePath = Join-Path $selfTestRoot 'unsafe-then-safe.ps1'
        $dynamicPath = Join-Path $selfTestRoot 'dynamic-splat.ps1'
        $dynamicContainedPath = Join-Path $selfTestRoot 'dynamic-splat-contained.ps1'
        $dynamicValuePath = Join-Path $selfTestRoot 'dynamic-value.ps1'
        $currentConsumerShapePath = Join-Path $selfTestRoot 'current-consumer-shape.ps1'
        $allContainedPath = Join-Path $selfTestRoot 'all-contained.ps1'
        $parameterFormsPath = Join-Path $selfTestRoot 'parameter-forms.ps1'
        [IO.File]::WriteAllText($directPath, '& pwsh -NoProfile')
        [IO.File]::WriteAllText($processPath, "Start-Process 'example.exe'")
        [IO.File]::WriteAllText($safePath, "Invoke-NestedPwsh -ArgumentList @('-NoProfile')")
        [IO.File]::WriteAllText($safeThenUnsafePath, @"
Start-Process -WindowStyle Hidden 'safe.exe'
Start-Process 'unsafe.exe'
"@)
        [IO.File]::WriteAllText($unsafeThenSafePath, @"
Start-Process 'unsafe.exe'
Start-Process -NoNewWindow 'safe.exe'
"@)
        [IO.File]::WriteAllText($dynamicPath, @"
`$parameters = @{ FilePath = 'example.exe' }
Start-Process @parameters
"@)
        [IO.File]::WriteAllText($dynamicContainedPath, @"
`$parameters = @{ FilePath = 'example.exe' }
Start-Process @parameters -WindowStyle Hidden
"@)
        [IO.File]::WriteAllText($dynamicValuePath, @"
`$style = 'Hidden'
Start-Process -WindowStyle `$style 'example.exe'
"@)
        [IO.File]::WriteAllText($currentConsumerShapePath, @"
`$startInfo = [Diagnostics.ProcessStartInfo]::new()
`$startInfo.CreateNoWindow = `$true
`$startProcessParameters = @{ FilePath = 'example.exe' }
`$startProcessParameters.WindowStyle = 'Hidden'
Start-Process @startProcessParameters
"@)
        [IO.File]::WriteAllText($allContainedPath, @"
Start-Process -WindowStyle 'Hidden' 'first.exe'
Start-Process -NoNewWindow 'second.exe'
"@)
        [IO.File]::WriteAllText($parameterFormsPath, @"
Start-Process -WindowStyle=Hidden 'first.exe'
Start-Process -NoNewWindow:`$true 'second.exe'
"@)
        if (@(Get-LaunchViolations $directPath).Count -eq 0) {
            throw 'The guard self-test did not reject a direct nested PowerShell launch.'
        }
        if (@(Get-LaunchViolations $processPath).Count -eq 0) {
            throw 'The guard self-test did not reject a visible Start-Process launch.'
        }
        if (@(Get-LaunchViolations $safePath).Count -ne 0) {
            throw 'The guard self-test rejected a helper-mediated launch.'
        }
        if (@(Get-LaunchViolations $safeThenUnsafePath).Count -ne 1) {
            throw 'The guard self-test did not reject the unsafe sibling after a contained Start-Process site.'
        }
        if (@(Get-LaunchViolations $unsafeThenSafePath).Count -ne 1) {
            throw 'The guard self-test did not reject the unsafe sibling before a contained Start-Process site.'
        }
        if (@(Get-LaunchViolations $dynamicPath).Count -ne 1) {
            throw 'The guard self-test did not reject an ambiguous splatted Start-Process site.'
        }
        if (@(Get-LaunchViolations $dynamicContainedPath).Count -ne 0) {
            throw 'The guard self-test rejected a splatted site with an explicit site-local hidden setting.'
        }
        if (@(Get-LaunchViolations $dynamicValuePath).Count -ne 1) {
            throw 'The guard self-test accepted a dynamic WindowStyle value without site-local proof.'
        }
        if (@(Get-LaunchViolations $currentConsumerShapePath).Count -ne 1) {
            throw 'The guard self-test did not reject the current package-consumer splat shape.'
        }
        if (@(Get-LaunchViolations $allContainedPath).Count -ne 0) {
            throw 'The guard self-test rejected a file whose Start-Process sites are all contained.'
        }
        if (@(Get-LaunchViolations $parameterFormsPath).Count -ne 0) {
            throw 'The guard self-test rejected supported attached parameter containment forms.'
        }
    }
    finally {
        Remove-Item -LiteralPath $selfTestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Output 'Nested PowerShell launch guard self-test passed.'
    exit 0
}

if (-not (Test-Path -LiteralPath $helperPath -PathType Leaf)) {
    throw "Shared nested PowerShell launch helper is missing: $helperPath"
}

$scriptFiles = Get-ChildItem -LiteralPath $repositoryRoot -Recurse -File -Filter '*.ps1' |
    Where-Object {
        $_.FullName -notin @($helperPath, $guardPath) -and
        $_.FullName -notmatch '[\\/]((\.git)|(bin)|(obj)|(artifacts)|_probe[\\/]corpus)([\\/]|$)'
    }
$violations = @($scriptFiles | ForEach-Object { Get-LaunchViolations $_.FullName })
if ($violations.Count -gt 0) {
    throw "Visible child process launch sites must use the shared containment helper."
}

Write-Output 'Nested PowerShell launch guard passed.'
