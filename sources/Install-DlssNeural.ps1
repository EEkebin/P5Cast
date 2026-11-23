[CmdletBinding()]
param([string]$ArchivePath)
$ErrorActionPreference='Stop'
$packageUrl='https://d1oqksizpb0e3d.cloudfront.net/rtx-remix-ngx_sdk_dlnr@2.7z'
$packageHash='4BB8651B3AC2180F23DF1F39422C489810A13D445A94DC836C876C18C93ECEFE'
$runtimeHash='2BC19BC09A7B89DD44F73E2AC0108FCC586E2154BDD0830F242F4F040E28789F'
$licenseHash='3027F23CA5A46DD9CB8183FBD522983A86F64D7DAAC5982912BF9F214671F294'
$version='310.9.16'
$parent=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'P5Cast\providers\dlss-nr'))
$destination=Join-Path $parent $version
$id='setup-'+[Guid]::NewGuid().ToString('N')
$stage=Join-Path $parent $id
$report=Join-Path ([IO.Path]::GetTempPath()) ($id+'.json')
$exitCode=0
try {
    # Recheck at install time, even when called outside the dashboard.
    $player=Join-Path (Split-Path $PSScriptRoot) 'engine\bin\CadrivectPlayer.exe'
    $probe=Start-Process -FilePath $player -ArgumentList @('--capabilities','--renderer','vulkan','--report',('"'+$report+'"')) -WindowStyle Hidden -PassThru
    try {
        if(!$probe.WaitForExit(40000)) { $probe.Kill(); throw 'The Vulkan support check timed out.' }
        if($probe.ExitCode -ne 0 -or !(Test-Path -LiteralPath $report -PathType Leaf)) {throw 'The Vulkan support check failed.'}
    } finally {$probe.Dispose()}
    $gpu=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if($gpu.'dlss-nr' -is [bool] -and $gpu.'dlss-nr') {
        @{ok=$true;message='Neural rendering is already available.'} | ConvertTo-Json -Compress
    } else {
        if(!($gpu.dlssNeuralRuntimeDownloadAvailable -is [bool] -and $gpu.dlssNeuralRuntimeDownloadAvailable)) {
            throw 'This Vulkan adapter and driver did not qualify for the neural rendering runtime.'
        }
        [void][IO.Directory]::CreateDirectory($parent)
        # A concurrent downloader can finish while this process is probing.
        $lock=[IO.File]::Open((Join-Path $parent 'install.lock'),'OpenOrCreate','ReadWrite','None')
        try {
            [void][IO.Directory]::CreateDirectory($stage)
            if($ArchivePath) {$archive=(Resolve-Path -LiteralPath $ArchivePath).Path}
            else {
                $archive=Join-Path $stage 'sdk.7z'
                $curl=Join-Path ([Environment]::SystemDirectory) 'curl.exe'
                & $curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --retry 2 --output $archive $packageUrl
                if($LASTEXITCODE){throw 'The NVIDIA download failed. Check your network and try again.'}
            }
            if((Get-Item -LiteralPath $archive).Length -ne 367851723 -or (Get-FileHash -LiteralPath $archive).Hash -ne $packageHash) {
                throw 'The downloaded NVIDIA package did not match its expected size or SHA-256.'
            }
            $tar=Join-Path ([Environment]::SystemDirectory) 'tar.exe'
            & $tar -xf $archive -C $stage 'lib/Windows_x86_64/rel/nvngx_dlssnr.dll' 'LICENSE.txt'
            if($LASTEXITCODE){throw 'Windows could not extract the NVIDIA runtime.'}
            $runtime=Join-Path $stage 'lib\Windows_x86_64\rel\nvngx_dlssnr.dll'
            $license=Join-Path $stage 'LICENSE.txt'
            if((Get-FileHash -LiteralPath $runtime).Hash -ne $runtimeHash -or (Get-FileHash -LiteralPath $license).Hash -ne $licenseHash) {
                throw 'The extracted NVIDIA files did not match their pinned SHA-256 values.'
            }
            $signature=Get-AuthenticodeSignature -LiteralPath $runtime
            if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(?:^|, )O=NVIDIA Corporation(?:,|$)') {
                throw 'The NVIDIA runtime publisher signature could not be verified.'
            }
            [void][IO.Directory]::CreateDirectory($destination)
            foreach($sourceFile in $license,$runtime) {
                $target=Join-Path $destination ([IO.Path]::GetFileName($sourceFile))
                if([IO.File]::Exists($target)){[IO.File]::Replace($sourceFile,$target,(Join-Path $stage ('previous-'+[IO.Path]::GetFileName($sourceFile))))}
                else {[IO.File]::Move($sourceFile,$target)}
            }
            @{ok=$true;message='NVIDIA neural rendering runtime installed.'} | ConvertTo-Json -Compress
        } finally {$lock.Dispose()}
    }
} catch {
    @{ok=$false;message=$_.Exception.Message} | ConvertTo-Json -Compress
    $exitCode=1
} finally {
    if(Test-Path -LiteralPath $report) {Remove-Item -LiteralPath $report -Force}
    $resolvedStage=[IO.Path]::GetFullPath($stage)
    if([IO.Path]::GetDirectoryName($resolvedStage) -eq $parent -and [IO.Path]::GetFileName($resolvedStage) -eq $id -and [IO.Directory]::Exists($resolvedStage)) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
exit $exitCode
