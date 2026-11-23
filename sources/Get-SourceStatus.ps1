$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SourceTools.ps1')
$sonyPath = Find-SonyRemotePlay
[pscustomobject]@{
    Sony = [pscustomobject]@{ installed = [bool]$sonyPath; executable = $sonyPath }
    Direct = [pscustomobject]@{ installed = (Test-Path -LiteralPath $chiakiExe -PathType Leaf); version = $chiakiVersion; executable = $chiakiExe }
    Demo = [pscustomobject]@{ installed = (Test-Path -LiteralPath (Get-DemoPath) -PathType Leaf); executable = (Get-DemoPath) }
} | ConvertTo-Json -Depth 3 -Compress
