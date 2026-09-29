[CmdletBinding()]
param(
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$helperPath = Join-Path $PSScriptRoot 'Invoke-NestedPwsh.ps1'
$guardPath = $PSCommandPath

function Get-ParsedCommandRecords(
    [string]$Text,
    [string]$Path,
    [System.Management.Automation.Language.Ast]$InitialAst
) {
    $pending = [System.Collections.Generic.Queue[object]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pending.Enqueue([pscustomobject]@{
            Text = $Text
            Ast = $InitialAst
            BaseLine = 0
            Embedded = $false
        })

    while ($pending.Count -gt 0) {
        $item = $pending.Dequeue()
        if ([string]::IsNullOrWhiteSpace($item.Text) -or -not $seen.Add($item.Text)) {
            continue
        }

        $ast = $item.Ast
        if ($null -eq $ast) {
            $tokens = $null
            $parseErrors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseInput(
                $item.Text,
                [ref]$tokens,
                [ref]$parseErrors)
            if ($parseErrors.Count -gt 0) {
                continue
            }
        }

        foreach ($command in @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true))) {
            [pscustomobject]@{
                Path = $Path
                BaseLine = $item.BaseLine
                Command = $command
            }
        }

        foreach ($stringAst in @($ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.StringConstantExpressionAst] -or
                    $node -is [System.Management.Automation.Language.ExpandableStringExpressionAst]
                }, $true))) {
            $value = [string]$stringAst.Value
            $isHereString = $stringAst -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
                ([string]$stringAst.StringConstantType -match 'HereString')
            $isScriptLike = $value -match '(?i)(?:\r?\n|(?:^|\s)(?:Start-Process|pwsh(?:\.exe)?|powershell(?:\.exe)?|Invoke-Expression)\b\s+\S)'
            if (-not ($isHereString -or ($item.Embedded -and $isScriptLike))) {
                continue
            }

            $pending.Enqueue([pscustomobject]@{
                    Text = $value
                    Ast = $null
                    BaseLine = $item.BaseLine + $stringAst.Extent.StartLineNumber - 1
                    Embedded = $true
                })
        }
    }
}

function Get-LaunchViolations([string]$Path) {
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        return @("${Path} contains PowerShell parse errors.")
    }

    $violations = [System.Collections.Generic.List[string]]::new()
    $source = [IO.File]::ReadAllText($Path)
    $commands = @(Get-ParsedCommandRecords -Text $source -Path $Path -InitialAst $ast)
    $assignments = @($ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.AssignmentStatementAst]
            }, $true))
    foreach ($record in $commands) {
        $command = $record.Command
        $lineNumber = $record.BaseLine + $command.Extent.StartLineNumber
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
            [void]$violations.Add("${Path}:$lineNumber`: direct nested PowerShell launch")
            continue
        }

        $literalArguments = @($command.CommandElements | Select-Object -Skip 1 | Where-Object {
                $_ -is [System.Management.Automation.Language.StringConstantExpressionAst]
            } | ForEach-Object { $_.Value })
        if ($commandName -notmatch '^(?i:Invoke-NestedPwsh|Invoke-NestedProcess)$' -and
            $literalArguments | Where-Object { $_ -match '^(?i:pwsh|powershell)(?:\.exe)?$' }) {
            [void]$violations.Add("${Path}:$lineNumber`: nested PowerShell executable passed to '$commandName'")
        }

        $hasHiddenContainment = $command.Extent.Text -match '(?i)(?:-\s*WindowStyle\s*(?:=|\s)\s*[''"]?Hidden[''"]?(?=\s|$)|(?<!\w)-NoNewWindow(?=\s|$))'
        if (-not $hasHiddenContainment -and $commandName -eq 'Start-Process') {
            foreach ($splat in @($command.CommandElements | Where-Object {
                        $_ -is [System.Management.Automation.Language.VariableExpressionAst] -and $_.Splatted
                    })) {
                $variableName = $splat.VariablePath.UserPath
                $basePattern = '^\$' + [regex]::Escape($variableName) + '$'
                $stylePattern = '^\$' + [regex]::Escape($variableName) + '(?:\.(?:WindowStyle|NoNewWindow)|\[[''"](?:WindowStyle|NoNewWindow)[''"]\])$'
                $latestBase = @($assignments | Where-Object {
                            $_.Extent.StartOffset -lt $command.Extent.StartOffset -and
                            $_.Left.Extent.Text -match $basePattern
                        } | Sort-Object { $_.Extent.StartOffset } | Select-Object -Last 1)
                $latestStyle = @($assignments | Where-Object {
                            $_.Extent.StartOffset -lt $command.Extent.StartOffset -and
                            $_.Left.Extent.Text -match $stylePattern
                        } | Sort-Object { $_.Extent.StartOffset } | Select-Object -Last 1)
                if (($latestBase.Count -gt 0 -and $latestBase[0].Right.Extent.Text -match '(?i)(?:WindowStyle\s*=\s*[''"]Hidden[''"]|NoNewWindow\s*=\s*\$true)') -or
                    ($latestStyle.Count -gt 0 -and $latestStyle[0].Right.Extent.Text -match '(?i)^(?:[''"]Hidden[''"]|\$true)$')) {
                    $hasHiddenContainment = $true
                    break
                }
            }
        }
        if ($commandName -eq 'Start-Process' -and -not $hasHiddenContainment) {
            [void]$violations.Add("${Path}:$lineNumber`: Start-Process lacks hidden-window containment")
        }
    }

    return $violations.ToArray()
}

function Get-CSharpCodeMask([string]$Text) {
    $builder = [Text.StringBuilder]::new($Text.Length)
    $state = 'Code'
    $index = 0
    while ($index -lt $Text.Length) {
        $character = $Text[$index]
        $nextCharacter = if ($index + 1 -lt $Text.Length) { $Text[$index + 1] } else { [char]0 }
        if ($state -eq 'LineComment') {
            if ($character -eq "`r" -or $character -eq "`n") {
                [void]$builder.Append($character)
                $state = 'Code'
            }
            else {
                [void]$builder.Append(' ')
            }
            $index++
            continue
        }
        if ($state -eq 'BlockComment') {
            if ($character -eq '*' -and $nextCharacter -eq '/') {
                [void]$builder.Append('  ')
                $index += 2
                $state = 'Code'
            }
            elseif ($character -eq "`r" -or $character -eq "`n") {
                [void]$builder.Append($character)
                $index++
            }
            else {
                [void]$builder.Append(' ')
                $index++
            }
            continue
        }
        if ($state -eq 'String') {
            if ($character -eq '\') {
                [void]$builder.Append(' ')
                if ($index + 1 -lt $Text.Length) {
                    if ($Text[$index + 1] -eq "`r" -or $Text[$index + 1] -eq "`n") {
                        [void]$builder.Append($Text[$index + 1])
                    }
                    else {
                        [void]$builder.Append(' ')
                    }
                    $index += 2
                }
                else {
                    $index++
                }
            }
            elseif ($character -eq '"') {
                [void]$builder.Append(' ')
                $index++
                $state = 'Code'
            }
            elseif ($character -eq "`r" -or $character -eq "`n") {
                [void]$builder.Append($character)
                $index++
                $state = 'Code'
            }
            else {
                [void]$builder.Append(' ')
                $index++
            }
            continue
        }
        if ($state -eq 'VerbatimString') {
            if ($character -eq '"' -and $nextCharacter -eq '"') {
                [void]$builder.Append('  ')
                $index += 2
            }
            elseif ($character -eq '"') {
                [void]$builder.Append(' ')
                $index++
                $state = 'Code'
            }
            elseif ($character -eq "`r" -or $character -eq "`n") {
                [void]$builder.Append($character)
                $index++
            }
            else {
                [void]$builder.Append(' ')
                $index++
            }
            continue
        }
        if ($state -eq 'Char') {
            if ($character -eq '\') {
                [void]$builder.Append(' ')
                if ($index + 1 -lt $Text.Length) {
                    [void]$builder.Append(' ')
                    $index += 2
                }
                else {
                    $index++
                }
            }
            elseif ($character -eq "'") {
                [void]$builder.Append(' ')
                $index++
                $state = 'Code'
            }
            elseif ($character -eq "`r" -or $character -eq "`n") {
                [void]$builder.Append($character)
                $index++
                $state = 'Code'
            }
            else {
                [void]$builder.Append(' ')
                $index++
            }
            continue
        }

        if ($character -eq '/' -and $nextCharacter -eq '/') {
            [void]$builder.Append('  ')
            $index += 2
            $state = 'LineComment'
        }
        elseif ($character -eq '/' -and $nextCharacter -eq '*') {
            [void]$builder.Append('  ')
            $index += 2
            $state = 'BlockComment'
        }
        elseif ($character -eq '@' -and $nextCharacter -eq '"') {
            [void]$builder.Append('  ')
            $index += 2
            $state = 'VerbatimString'
        }
        elseif ($character -eq '"') {
            [void]$builder.Append(' ')
            $index++
            $state = 'String'
        }
        elseif ($character -eq "'") {
            [void]$builder.Append(' ')
            $index++
            $state = 'Char'
        }
        else {
            [void]$builder.Append($character)
            $index++
        }
    }
    return $builder.ToString()
}

function Get-CSharpInitializerBody([string]$Code, [int]$StartIndex) {
    $openBrace = $Code.IndexOf('{', $StartIndex)
    $semicolon = $Code.IndexOf(';', $StartIndex)
    if ($openBrace -lt 0 -or ($semicolon -ge 0 -and $semicolon -lt $openBrace)) {
        return $null
    }

    $depth = 0
    for ($index = $openBrace; $index -lt $Code.Length; $index++) {
        if ($Code[$index] -eq '{') {
            $depth++
        }
        elseif ($Code[$index] -eq '}') {
            $depth--
            if ($depth -eq 0) {
                return $Code.Substring($openBrace + 1, $index - $openBrace - 1)
            }
        }
    }
    return $null
}

function Get-CSharpLaunchAudit([string]$Path) {
    $source = [IO.File]::ReadAllText($Path)
    $code = Get-CSharpCodeMask $source
    $violations = [System.Collections.Generic.List[string]]::new()
    $constructors = @([regex]::Matches($code, '(?<![\w.])new\s+(?:[A-Za-z_]\w*\.)*ProcessStartInfo\b'))
    $starts = @([regex]::Matches($code, '(?<![\w.])(?:[A-Za-z_]\w*\.)*Process\.Start\s*\('))
    $allStartCalls = @([regex]::Matches($code, '(?<![\w.])(?:global::)?[A-Za-z_]\w*(?:(?:\.|::)[A-Za-z_]\w*)*\s*\.\s*Start\s*\('))
    $knownStartIndexes = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($start in $starts) {
        [void]$knownStartIndexes.Add($start.Index)
    }
    $unsupportedStartCalls = @($allStartCalls | Where-Object { -not $knownStartIndexes.Contains($_.Index) })
    $safeVariables = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $constructorRecords = [System.Collections.Generic.List[object]]::new()

    foreach ($constructor in $constructors) {
        $body = Get-CSharpInitializerBody -Code $code -StartIndex $constructor.Index
        $hasSafeShell = $null -ne $body -and $body -match '(?m)\bUseShellExecute\s*=\s*false\b'
        $hasNoWindow = $null -ne $body -and $body -match '(?m)\bCreateNoWindow\s*=\s*true\b'
        if (-not ($hasSafeShell -and $hasNoWindow)) {
            $lineNumber = 1 + ($source.Substring(0, $constructor.Index) -split "`n").Count - 1
            [void]$violations.Add("${Path}:$lineNumber`: ProcessStartInfo must set UseShellExecute = false and CreateNoWindow = true")
        }

        $assignment = [regex]::Match(
            $code.Substring(0, $constructor.Index),
            '(?s)(?:\b(?:var|ProcessStartInfo)\s+)?(?<name>[A-Za-z_]\w*)\s*=\s*$')
        $variableName = if ($assignment.Success) { $assignment.Groups['name'].Value } else { $null }
        $postConstructionMutation = $false
        if ($variableName) {
            $mutation = [regex]::Match(
                $code.Substring($constructor.Index + $constructor.Length),
                '(?m)\b' + [regex]::Escape($variableName) + '\s*\.\s*(?<property>UseShellExecute|CreateNoWindow)\s*=')
            if ($mutation.Success) {
                $postConstructionMutation = $true
                $mutationIndex = $constructor.Index + $constructor.Length + $mutation.Index
                $mutationLine = 1 + ($source.Substring(0, $mutationIndex) -split "`n").Count - 1
                [void]$violations.Add("${Path}:$mutationLine`: ProcessStartInfo property '$($mutation.Groups['property'].Value)' must not be changed after construction")
            }
        }
        [void]$constructorRecords.Add([pscustomobject]@{
                Index = $constructor.Index
                Safe = $hasSafeShell -and $hasNoWindow -and -not $postConstructionMutation
                Variable = $variableName
            })
        if ($variableName -and $hasSafeShell -and $hasNoWindow -and -not $postConstructionMutation) {
            [void]$safeVariables.Add($variableName)
        }
    }

    $safeProcessVariables = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $processAssignments = [regex]::Matches($code, '(?s)(?<name>[A-Za-z_]\w*)\s*=\s*new\s+(?:[A-Za-z_]\w*\.)*Process\s*\{')
    foreach ($assignment in $processAssignments) {
        $newIndex = $assignment.Index + $assignment.Value.LastIndexOf('new')
        $body = Get-CSharpInitializerBody -Code $code -StartIndex $newIndex
        if ($null -eq $body -or $body -notmatch '\bStartInfo\s*=') {
            continue
        }
        $safeInlineStartInfo = $body -match '\bStartInfo\s*=\s*new\s+(?:[A-Za-z_]\w*\.)*ProcessStartInfo\b' -and
            $body -match '\bUseShellExecute\s*=\s*false\b' -and
            $body -match '\bCreateNoWindow\s*=\s*true\b'
        $namedStartInfo = [regex]::Match($body, '\bStartInfo\s*=\s*(?<name>[A-Za-z_]\w*)')
        if ($safeInlineStartInfo -or ($namedStartInfo.Success -and $safeVariables.Contains($namedStartInfo.Groups['name'].Value))) {
            [void]$safeProcessVariables.Add($assignment.Groups['name'].Value)
        }
    }

    foreach ($start in $starts) {
        $argumentText = $code.Substring($start.Index + $start.Length)
        $argument = [regex]::Match($argumentText, '^\s*(?<value>[A-Za-z_]\w*)(?:\s*,|\s*\))')
        if (-not $argument.Success) {
            $directConstructor = @($constructorRecords | Where-Object {
                    $_.Index -gt $start.Index -and
                    $_.Index -lt ($start.Index + $start.Length + 32)
                } | Select-Object -First 1)
            if ($directConstructor.Count -gt 0 -and $directConstructor[0].Safe) {
                continue
            }
            $lineNumber = 1 + ($source.Substring(0, $start.Index) -split "`n").Count - 1
            [void]$violations.Add("${Path}:$lineNumber`: Process.Start must use a contained ProcessStartInfo")
            continue
        }
        $variableName = $argument.Groups['value'].Value
        if (-not $safeVariables.Contains($variableName)) {
            $lineNumber = 1 + ($source.Substring(0, $start.Index) -split "`n").Count - 1
            [void]$violations.Add("${Path}:$lineNumber`: Process.Start argument '$variableName' is not backed by a contained ProcessStartInfo")
        }
    }

    foreach ($unsupportedStart in $unsupportedStartCalls) {
        $receiver = [regex]::Match($unsupportedStart.Value, '^(?<name>(?:global::)?[A-Za-z_]\w*(?:(?:\.|::)[A-Za-z_]\w*)*)\s*\.\s*Start').Groups['name'].Value
        if ($receiver -and $safeProcessVariables.Contains($receiver)) {
            continue
        }
        $lineNumber = 1 + ($source.Substring(0, $unsupportedStart.Index) -split "`n").Count - 1
        [void]$violations.Add("${Path}:$lineNumber`: unsupported C# process-launch form '$($unsupportedStart.Value.Trim())'")
    }

    return [pscustomobject]@{
        Path = $Path
        LaunchCount = $constructors.Count + $allStartCalls.Count
        Violations = $violations.ToArray()
    }
}

if ($SelfTest) {
    $selfTestRoot = Join-Path ([IO.Path]::GetTempPath()) "nested-pwsh-guard-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $selfTestRoot -Force | Out-Null
    try {
        $directPath = Join-Path $selfTestRoot 'direct.ps1'
        $processPath = Join-Path $selfTestRoot 'process.ps1'
        $embeddedPath = Join-Path $selfTestRoot 'embedded.ps1'
        $embeddedSafePath = Join-Path $selfTestRoot 'embedded-safe.ps1'
        $splatSafePath = Join-Path $selfTestRoot 'splat-safe.ps1'
        $splatNoNewWindowPath = Join-Path $selfTestRoot 'splat-nonewwindow-safe.ps1'
        $unsafeCSharpPath = Join-Path $selfTestRoot 'unsafe.cs'
        $unsafeMutationCSharpPath = Join-Path $selfTestRoot 'unsafe-mutation.cs'
        $unsafeInstanceCSharpPath = Join-Path $selfTestRoot 'unsafe-instance.cs'
        $unsafeQualifiedCSharpPath = Join-Path $selfTestRoot 'unsafe-qualified.cs'
        $unsafeAliasCSharpPath = Join-Path $selfTestRoot 'unsafe-alias.cs'
        $safeCSharpPath = Join-Path $selfTestRoot 'safe.cs'
        $safePath = Join-Path $selfTestRoot 'safe.ps1'
        [IO.File]::WriteAllText($directPath, '& pwsh -NoProfile')
        [IO.File]::WriteAllText($processPath, "Start-Process 'example.exe'")
        [IO.File]::WriteAllText($embeddedPath, @'
$nested = @"
Start-Process -FilePath 'example.exe'
"@
Invoke-NestedPwsh -ArgumentList $nested
'@)
        [IO.File]::WriteAllText($embeddedSafePath, @'
$nested = @"
Start-Process -FilePath 'example.exe' -WindowStyle Hidden
"@
Invoke-NestedPwsh -ArgumentList $nested
'@)
        [IO.File]::WriteAllText($splatSafePath, @'
$parameters = @{ FilePath = 'example.exe' }
$parameters.WindowStyle = 'Hidden'
Start-Process @parameters
'@)
        [IO.File]::WriteAllText($splatNoNewWindowPath, @'
$parameters = @{ FilePath = 'example.exe' }
$parameters.NoNewWindow = $true
Start-Process @parameters
'@)
        [IO.File]::WriteAllText($unsafeCSharpPath, @'
using System.Diagnostics;
var startInfo = new ProcessStartInfo("pwsh") { UseShellExecute = true };
Process.Start(startInfo);
'@)
        [IO.File]::WriteAllText($unsafeMutationCSharpPath, @'
using System.Diagnostics;
var startInfo = new ProcessStartInfo("pwsh")
{
    UseShellExecute = false,
    CreateNoWindow = true
};
startInfo.UseShellExecute = true;
Process.Start(startInfo);
'@)
        [IO.File]::WriteAllText($unsafeInstanceCSharpPath, @'
using System.Diagnostics;
var process = new Process();
process.Start();
'@)
        [IO.File]::WriteAllText($unsafeQualifiedCSharpPath, @'
using System.Diagnostics;
var startInfo = new ProcessStartInfo("pwsh")
{
    UseShellExecute = false,
    CreateNoWindow = true
};
global::System.Diagnostics.Process.Start(startInfo);
'@)
        [IO.File]::WriteAllText($unsafeAliasCSharpPath, @'
using ProcessAlias = System.Diagnostics.Process;
using System.Diagnostics;
var startInfo = new ProcessStartInfo("pwsh")
{
    UseShellExecute = false,
    CreateNoWindow = true
};
ProcessAlias.Start(startInfo);
'@)
        [IO.File]::WriteAllText($safeCSharpPath, @'
using System.Diagnostics;
Process.Start(new System.Diagnostics.ProcessStartInfo("pwsh")
{
    UseShellExecute = false,
    CreateNoWindow = true
});
'@)
        [IO.File]::WriteAllText($safePath, "Invoke-NestedPwsh -ArgumentList @('-NoProfile')")
        if (@(Get-LaunchViolations $directPath).Count -eq 0) {
            throw 'The guard self-test did not reject a direct nested PowerShell launch.'
        }
        if (@(Get-LaunchViolations $processPath).Count -eq 0) {
            throw 'The guard self-test did not reject a visible Start-Process launch.'
        }
        if (@(Get-LaunchViolations $embeddedPath).Count -eq 0) {
            throw 'The guard self-test did not reject a visible Start-Process launch embedded in a here-string.'
        }
        if (@(Get-LaunchViolations $embeddedSafePath).Count -ne 0) {
            throw 'The guard self-test rejected a hidden Start-Process launch embedded in a here-string.'
        }
        if (@(Get-LaunchViolations $splatSafePath).Count -ne 0) {
            throw 'The guard self-test rejected a hidden Start-Process launch supplied through a splatted parameter set.'
        }
        if (@(Get-LaunchViolations $splatNoNewWindowPath).Count -ne 0) {
            throw 'The guard self-test rejected a no-new-window Start-Process launch supplied through a splatted parameter set.'
        }
        if (@(Get-LaunchViolations $safePath).Count -ne 0) {
            throw 'The guard self-test rejected a helper-mediated launch.'
        }
        $unsafeCSharpAudit = Get-CSharpLaunchAudit $unsafeCSharpPath
        if ($unsafeCSharpAudit.LaunchCount -ne 2 -or @($unsafeCSharpAudit.Violations).Count -eq 0) {
            throw 'The guard self-test did not reject an unsafe C# process launch.'
        }
        $unsafeMutationCSharpAudit = Get-CSharpLaunchAudit $unsafeMutationCSharpPath
        if ($unsafeMutationCSharpAudit.LaunchCount -ne 2 -or @($unsafeMutationCSharpAudit.Violations).Count -eq 0) {
            throw 'The guard self-test did not reject post-construction C# process-launch mutation.'
        }
        $unsafeInstanceCSharpAudit = Get-CSharpLaunchAudit $unsafeInstanceCSharpPath
        if ($unsafeInstanceCSharpAudit.LaunchCount -ne 1 -or @($unsafeInstanceCSharpAudit.Violations).Count -eq 0) {
            throw 'The guard self-test did not reject an instance C# process launch.'
        }
        $unsafeQualifiedCSharpAudit = Get-CSharpLaunchAudit $unsafeQualifiedCSharpPath
        if ($unsafeQualifiedCSharpAudit.LaunchCount -ne 2 -or @($unsafeQualifiedCSharpAudit.Violations).Count -eq 0) {
            throw 'The guard self-test did not reject an unsupported qualified C# process launch form.'
        }
        $unsafeAliasCSharpAudit = Get-CSharpLaunchAudit $unsafeAliasCSharpPath
        if ($unsafeAliasCSharpAudit.LaunchCount -ne 2 -or @($unsafeAliasCSharpAudit.Violations).Count -eq 0) {
            throw 'The guard self-test did not reject an aliased C# process launch form.'
        }
        $safeCSharpAudit = Get-CSharpLaunchAudit $safeCSharpPath
        if ($safeCSharpAudit.LaunchCount -ne 2 -or @($safeCSharpAudit.Violations).Count -ne 0) {
            throw 'The guard self-test rejected a contained C# process launch.'
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

$csharpFiles = Get-ChildItem -LiteralPath $repositoryRoot -Recurse -File -Filter '*.cs' |
    Where-Object {
        $_.FullName -notmatch '[\\/]((\.git)|(bin)|(obj)|(artifacts)|_probe[\\/]corpus)([\\/]|$)'
    }
$csharpAudits = @($csharpFiles | ForEach-Object { Get-CSharpLaunchAudit $_.FullName })
$csharpLaunchCount = ($csharpAudits | ForEach-Object { $_.LaunchCount } | Measure-Object -Sum).Sum
if ($csharpFiles.Count -eq 0 -or $csharpLaunchCount -eq 0) {
    throw 'C# process-launch guard found no ProcessStartInfo or Process.Start site to audit.'
}
$csharpViolations = @($csharpAudits | ForEach-Object { $_.Violations })
if ($csharpViolations.Count -gt 0) {
    throw "C# process-launch sites must use contained ProcessStartInfo instances.`n$($csharpViolations -join [Environment]::NewLine)"
}

Write-Output 'Nested PowerShell launch guard passed.'
