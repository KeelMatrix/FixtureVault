[CmdletBinding()]
param(
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$helperPath = Join-Path $PSScriptRoot 'Invoke-NestedPwsh.ps1'
$guardPath = $PSCommandPath

function Add-LaunchViolation {
    param(
        [System.Collections.Generic.List[string]]$Violations,
        [string]$Path,
        [int]$Line,
        [string]$Message
    )

    [void]$Violations.Add("${Path}:$Line`: $Message")
}

function Get-LaunchViolations([string]$Path) {
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) {
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
            Add-LaunchViolation $violations $Path $command.Extent.StartLineNumber 'direct nested PowerShell launch'
        }

        $literalArguments = @($command.CommandElements | Select-Object -Skip 1 | Where-Object {
                $_ -is [System.Management.Automation.Language.StringConstantExpressionAst]
            } | ForEach-Object { $_.Value })
        if ($commandName -notin @('Invoke-NestedPwsh', 'Invoke-NestedProcess') -and
            ($literalArguments | Where-Object { $_ -match '^(?i:pwsh|powershell)(?:\.exe)?$' })) {
            Add-LaunchViolation $violations $Path $command.Extent.StartLineNumber "nested PowerShell executable passed to '$commandName'"
        }

        if ($commandName -ieq 'Start-Process') {
            Add-LaunchViolation $violations $Path $command.Extent.StartLineNumber 'direct Start-Process launch; use the shared process helper'
        }

        if ($commandName -ieq 'New-Object' -and
            ($literalArguments | Where-Object { $_ -match '(?i)(?:^|\.)ProcessStartInfo$' })) {
            Add-LaunchViolation $violations $Path $command.Extent.StartLineNumber 'direct ProcessStartInfo construction; use the shared process helper'
        }
    }

    $typeExpressions = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.TypeExpressionAst] }, $true)
    foreach ($typeExpression in $typeExpressions) {
        if ($typeExpression.TypeName.FullName -match '(?i)(?:^|\.)ProcessStartInfo$') {
            Add-LaunchViolation $violations $Path $typeExpression.Extent.StartLineNumber 'direct ProcessStartInfo construction; use the shared process helper'
        }
    }

    $startInvocations = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst] }, $true) |
        Where-Object { $_.Member.Extent.Text.Trim() -ieq 'Start' }
    foreach ($startInvocation in $startInvocations) {
        Add-LaunchViolation $violations $Path $startInvocation.Extent.StartLineNumber 'direct Process.Start() launch; use the shared process helper'
    }

    return $violations.ToArray()
}

if ($SelfTest) {
    $selfTestRoot = Join-Path ([IO.Path]::GetTempPath()) "nested-pwsh-guard-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $selfTestRoot -Force | Out-Null
    try {
        $directPath = Join-Path $selfTestRoot 'direct.ps1'
        $safeHelperPath = Join-Path $selfTestRoot 'safe-helper.ps1'
        $startProcessPath = Join-Path $selfTestRoot 'start-process.ps1'
        $literalFileNamePath = Join-Path $selfTestRoot 'literal-file-name.ps1'
        $dynamicFileNamePath = Join-Path $selfTestRoot 'dynamic-file-name.ps1'
        $argumentWrapperPath = Join-Path $selfTestRoot 'argument-list-wrapper.ps1'
        $directProcessStartPath = Join-Path $selfTestRoot 'direct-process-start.ps1'
        $safeAssignmentPath = Join-Path $selfTestRoot 'safe-assignment-order.ps1'
        $unsafeAssignmentPath = Join-Path $selfTestRoot 'unsafe-assignment-order.ps1'
        $missingWindowPath = Join-Path $selfTestRoot 'missing-create-no-window.ps1'
        $falseWindowPath = Join-Path $selfTestRoot 'false-create-no-window.ps1'
        $currentReleaseShapePath = Join-Path $selfTestRoot 'current-release-shape.ps1'
        $currentConsumerShapePath = Join-Path $selfTestRoot 'current-consumer-shape.ps1'
        $safeThenUnsafePath = Join-Path $selfTestRoot 'safe-then-unsafe.ps1'
        $unsafeThenSafePath = Join-Path $selfTestRoot 'unsafe-then-safe.ps1'

        [IO.File]::WriteAllText($directPath, '& pwsh -NoProfile')
        [IO.File]::WriteAllText($safeHelperPath, "Invoke-NestedPwsh -ArgumentList @('-NoProfile')")
        [IO.File]::WriteAllText($startProcessPath, "Start-Process -WindowStyle Hidden -FilePath 'pwsh'")
        [IO.File]::WriteAllText($literalFileNamePath, @"
`$startInfo = [Diagnostics.ProcessStartInfo]::new()
`$startInfo.FileName = 'pwsh'
`$startInfo.ArgumentList.Add('-NoProfile')
"@)
        [IO.File]::WriteAllText($dynamicFileNamePath, @"
`$executable = 'pwsh'
`$startInfo = [Diagnostics.ProcessStartInfo]::new()
`$startInfo.FileName = `$executable
"@)
        [IO.File]::WriteAllText($argumentWrapperPath, @"
`$arguments = @('-NoProfile')
`$startInfo = [Diagnostics.ProcessStartInfo]::new()
`$startInfo.FileName = 'pwsh'
`$startInfo.ArgumentList.AddRange(`$arguments)
"@)
        [IO.File]::WriteAllText($directProcessStartPath, "[Diagnostics.Process]::Start('pwsh')")
        [IO.File]::WriteAllText($safeAssignmentPath, @"
`$startInfo = [Diagnostics.ProcessStartInfo]::new()
`$startInfo.UseShellExecute = `$false
`$startInfo.CreateNoWindow = `$true
`$startInfo.FileName = 'pwsh'
"@)
        [IO.File]::WriteAllText($unsafeAssignmentPath, @"
`$startInfo = [Diagnostics.ProcessStartInfo]::new()
`$startInfo.FileName = 'pwsh'
`$startInfo.UseShellExecute = `$true
`$startInfo.CreateNoWindow = `$true
"@)
        [IO.File]::WriteAllText($missingWindowPath, @"
`$startInfo = [Diagnostics.ProcessStartInfo]::new()
`$startInfo.FileName = 'pwsh'
"@)
        [IO.File]::WriteAllText($falseWindowPath, @"
`$startInfo = [Diagnostics.ProcessStartInfo]::new()
`$startInfo.UseShellExecute = `$false
`$startInfo.CreateNoWindow = `$false
`$startInfo.FileName = 'pwsh'
"@)
        [IO.File]::WriteAllText($currentReleaseShapePath, @"
`$startInfo = [Diagnostics.ProcessStartInfo]::new()
`$startInfo.FileName = 'pwsh'
`$startInfo.UseShellExecute = `$false
`$startInfo.CreateNoWindow = `$true
`$startInfo.RedirectStandardOutput = `$true
`$startInfo.RedirectStandardError = `$true
`$process = [Diagnostics.Process]::new()
`$process.StartInfo = `$startInfo
`$process.Start()
"@)
        [IO.File]::WriteAllText($currentConsumerShapePath, @"
`$startInfo = [Diagnostics.ProcessStartInfo]::new()
`$startInfo.FileName = `$Executable
`$startInfo.WorkingDirectory = (Get-Location).Path
`$startInfo.UseShellExecute = `$false
`$startInfo.CreateNoWindow = `$true
`$startInfo.ArgumentList.AddRange(`$Arguments)
`$process = [Diagnostics.Process]::new()
`$process.StartInfo = `$startInfo
`$process.Start()
"@)
        [IO.File]::WriteAllText($safeThenUnsafePath, @"
Start-Process -WindowStyle Hidden -FilePath 'safe.exe'
Start-Process -FilePath 'unsafe.exe'
"@)
        [IO.File]::WriteAllText($unsafeThenSafePath, @"
Start-Process -FilePath 'unsafe.exe'
Start-Process -NoNewWindow -FilePath 'safe.exe'
"@)

        if (@(Get-LaunchViolations $directPath).Count -eq 0) {
            throw 'The guard self-test did not reject a direct nested PowerShell launch.'
        }
        if (@(Get-LaunchViolations $safeHelperPath).Count -ne 0) {
            throw 'The guard self-test rejected a shared-helper launch site.'
        }
        foreach ($case in @(
                @{ Path = $startProcessPath; Name = 'direct Start-Process' },
                @{ Path = $literalFileNamePath; Name = 'literal FileName ProcessStartInfo' },
                @{ Path = $dynamicFileNamePath; Name = 'dynamic FileName ProcessStartInfo' },
                @{ Path = $argumentWrapperPath; Name = 'ArgumentList wrapper' },
                @{ Path = $directProcessStartPath; Name = 'direct Process.Start()' },
                @{ Path = $safeAssignmentPath; Name = 'safe property assignment ordering' },
                @{ Path = $unsafeAssignmentPath; Name = 'unsafe property assignment ordering' },
                @{ Path = $missingWindowPath; Name = 'missing CreateNoWindow' },
                @{ Path = $falseWindowPath; Name = 'false CreateNoWindow' },
                @{ Path = $currentReleaseShapePath; Name = 'current release-contract shape' },
                @{ Path = $currentConsumerShapePath; Name = 'current package-consumer shape' }
            )) {
            if (@(Get-LaunchViolations $case.Path).Count -eq 0) {
                throw "The guard self-test did not reject $($case.Name)."
            }
        }
        if (@(Get-LaunchViolations $safeThenUnsafePath).Count -lt 2) {
            throw 'The guard self-test did not reject every launch in the safe-then-unsafe mixed file.'
        }
        if (@(Get-LaunchViolations $unsafeThenSafePath).Count -lt 2) {
            throw 'The guard self-test did not reject every launch in the unsafe-then-safe mixed file.'
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
    throw "Visible child process launch sites must use the shared process helper.`n$($violations -join [Environment]::NewLine)"
}

Write-Output 'Nested PowerShell launch guard passed.'
