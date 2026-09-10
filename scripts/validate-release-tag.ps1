[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$tag = $env:RELEASE_TAG
if ([string]::IsNullOrWhiteSpace($tag) -or $tag -cne "v0.1.0") {
    throw "Unsupported release tag '$tag'. The first release accepts exactly v0.1.0."
}

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    "version=0.1.0" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
}

Write-Output "version=0.1.0"
