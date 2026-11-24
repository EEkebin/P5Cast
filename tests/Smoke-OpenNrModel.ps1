# The negative cases run offline. Pass the pinned NVIDIA archive to exercise a full install and repair.
[CmdletBinding()]
param([string]$ArchivePath)
$ErrorActionPreference='Stop'
$savedLocal=$env:LOCALAPPDATA
$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$id='P5Cast-OpenNrSetup-'+[guid]::NewGuid().ToString('N')
$folder=[IO.Directory]::CreateDirectory((Join-Path $temp $id)).FullName
$setup=Join-Path $PSScriptRoot '..\sources\Install-OpenNrModel.ps1'
$ps=Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
function Run-Setup([string[]]$Options,[bool]$ShouldPass) {
    $result=& $ps -NoProfile -ExecutionPolicy Bypass -File $setup @Options
    $code=$LASTEXITCODE
    $report=$result | ConvertFrom-Json
    if(($code -eq 0) -ne $ShouldPass -or [bool]$report.ok -ne $ShouldPass){throw "Unexpected setup result ($code) $result"}
    return $report
}
try {
    $env:LOCALAPPDATA=$folder
    $broken=Join-Path $folder 'bad.7z'
    [IO.File]::WriteAllText($broken,'invalid archive')
    $userFolder=[IO.Directory]::CreateDirectory((Join-Path $folder 'My models')).FullName
    $bad=Run-Setup @('-Destination',$userFolder,'-ArchivePath',$broken) $false
    if($bad.message -notmatch 'SHA-256' -or @(Get-ChildItem -Force -LiteralPath $userFolder).Count){throw 'Invalid archive was published or left staging files.'}
    $manifest=Join-Path $userFolder 'manifest.json'
    [IO.File]::WriteAllText($manifest,'keep this user file')
    $conflict=Run-Setup @('-Destination',$userFolder,'-ArchivePath',$broken) $false
    if($conflict.message -notmatch 'already exists' -or [IO.File]::ReadAllText($manifest) -ne 'keep this user file'){throw 'A custom model was overwritten.'}
    if($ArchivePath) {
        $first=Run-Setup @('-ArchivePath',$ArchivePath) $true
        $again=Run-Setup @('-ArchivePath',$broken) $true
        if($first.modelDirectory -ne $again.modelDirectory){throw 'Retry failed to reuse the verified model.'}
        $stage=Join-Path $first.modelDirectory 'model\stage-0.bin'
        [IO.File]::WriteAllText($stage,'retain damaged original')
        $repair=Run-Setup @('-ArchivePath',$ArchivePath) $true
        $retry=Run-Setup @('-ArchivePath',$broken) $true
        if($repair.modelDirectory -eq $first.modelDirectory -or $repair.modelDirectory -ne $retry.modelDirectory -or
            [IO.File]::ReadAllText($stage) -ne 'retain damaged original'){throw 'Default model recovery failed.'}
    }
    'OpenDLSS-NR model setup checks passed.'
} finally {
    $env:LOCALAPPDATA=$savedLocal
    if([IO.Path]::GetDirectoryName($folder) -eq $temp -and [IO.Path]::GetFileName($folder) -eq $id) {
        Remove-Item -LiteralPath $folder -Recurse -Force
    }
}
