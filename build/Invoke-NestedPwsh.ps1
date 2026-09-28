function Invoke-NestedPwsh {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)]
        [object[]]$ArgumentList
    )

    $pwshArguments = [System.Collections.Generic.List[object]]::new()
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Windows)) {
        [void]$pwshArguments.Add('-WindowStyle')
        [void]$pwshArguments.Add('Hidden')
    }

    for ($index = 0; $index -lt $ArgumentList.Count; $index++) {
        $argument = $ArgumentList[$index]
        if ($argument -is [string] -and $argument.EndsWith(':') -and
            $index + 1 -lt $ArgumentList.Count -and
            ($ArgumentList[$index + 1] -is [System.Management.Automation.SwitchParameter] -or
             $ArgumentList[$index + 1] -is [bool])) {
            $switch = $ArgumentList[++$index]
            if (($switch -is [System.Management.Automation.SwitchParameter] -and $switch.IsPresent) -or
                ($switch -is [bool] -and $switch)) {
                [void]$pwshArguments.Add($argument.Substring(0, $argument.Length - 1))
            }
            continue
        }
        [void]$pwshArguments.Add($argument)
    }

    $pwshExecutable = 'pwsh'
    $nativeArguments = $pwshArguments.ToArray()
    & $pwshExecutable @nativeArguments
    $global:LASTEXITCODE = $LASTEXITCODE
}

function Invoke-NestedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,

        [AllowEmptyCollection()]
        [string[]]$ArgumentList = @(),

        [string]$WorkingDirectory,

        [int]$TimeoutMilliseconds = 0
    )

    if ($TimeoutMilliseconds -lt 0) {
        throw 'TimeoutMilliseconds must be zero or greater.'
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $startInfo.WorkingDirectory = $WorkingDirectory
    }
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $ArgumentList) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start child command '$Executable'."
        }

        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        $timedOut = $false
        if ($TimeoutMilliseconds -gt 0) {
            $timedOut = -not $process.WaitForExit($TimeoutMilliseconds)
            if ($timedOut) {
                $process.Kill()
                $process.WaitForExit()
            }
        }
        else {
            $process.WaitForExit()
        }

        [pscustomobject]@{
            ExitCode = if ($timedOut) { $null } else { $process.ExitCode }
            StandardOutput = $standardOutputTask.GetAwaiter().GetResult()
            StandardError = $standardErrorTask.GetAwaiter().GetResult()
            TimedOut = $timedOut
        }
    }
    finally {
        $process.Dispose()
    }
}
