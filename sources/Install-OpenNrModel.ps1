# Optional per-user model setup. The release archive contains the reader, never the model.
[CmdletBinding()]
param([string]$Destination=(Join-Path $env:LOCALAPPDATA 'P5Cast\providers\open-nr\model'),[string]$ArchivePath)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Core') {
    # The setup reader uses the same .NET Framework serializer as the Windows app.
    $winps=Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
    $arguments=@('-Destination',$Destination)
    if($ArchivePath){$arguments+=@('-ArchivePath',$ArchivePath)}
    & $winps -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @arguments
    exit $LASTEXITCODE
}
$archiveDigest='4BB8651B3AC2180F23DF1F39422C489810A13D445A94DC836C876C18C93ECEFE'
$runtimeDigest='2BC19BC09A7B89DD44F73E2AC0108FCC586E2154BDD0830F242F4F040E28789F'
$licenseDigest='3027F23CA5A46DD9CB8183FBD522983A86F64D7DAAC5982912BF9F214671F294'
$manifestDigest='A418A39BC57684C80BD8539CB84B0058F3F8161511172E731C209FB0929590E4'
$packageUrl='https://d1oqksizpb0e3d.cloudfront.net/rtx-remix-ngx_sdk_dlnr@2.7z'
$stage=$null
$exitCode=0
function Check-Hash([string]$Path,[string]$Expected) {
    return [IO.File]::Exists($Path) -and (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash -eq $Expected
}
function Test-Complete([string]$Folder) {
    $manifest=Join-Path $Folder 'manifest.json'
    if(!(Check-Hash $manifest $manifestDigest) -or !(Check-Hash (Join-Path $Folder 'NVIDIA-LICENSE.txt') $licenseDigest)){return $false}
    foreach($part in (Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json).stages) {
        if(!(Check-Hash (Join-Path $Folder ('model\'+$part.file)) $part.sha256)){return $false}
    }
    return $true
}
try {
    $dest=[IO.Path]::GetFullPath($Destination)
    $directoryExists=[IO.Directory]::Exists($dest)
    $complete=Test-Complete $dest
    if($directoryExists -and !$complete) {
        if($dest -eq (Join-Path $env:LOCALAPPDATA 'P5Cast\providers\open-nr\model')) {
            # Recover from an incomplete default install without overwriting any model.
            $base=$dest
            for($copy=1; ; $copy++) {
                $dest="$base-repair-$copy"
                $complete=Test-Complete $dest
                if($complete -or !(Test-Path -LiteralPath $dest)){break}
            }
        } elseif([IO.File]::Exists((Join-Path $dest 'manifest.json'))) {throw 'A different or incomplete model already exists here. Choose another folder.'}
        # A selected existing folder is a parent directory. Publish into an owned child.
        else {
            $dest=Join-Path $dest 'OpenDLSS-NR-310.8.0'
            $complete=Test-Complete $dest
        }
    }
    if(!$complete) {
        if((Test-Path -LiteralPath $dest) -and !(Test-Complete $dest)){throw 'The destination already exists. Choose another folder.'}
        $parent=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dest)).FullName
        $id='.p5cast-model-'+[Guid]::NewGuid().ToString('N')
        $stage=[IO.Directory]::CreateDirectory((Join-Path $parent $id)).FullName
        if($ArchivePath) { $archive=(Resolve-Path -LiteralPath $ArchivePath).Path }
        else {
            $cache=Join-Path $env:LOCALAPPDATA 'P5Cast\build\neural-rendering-2\rtx-remix-ngx_sdk_dlnr@2.7z'
            if(Check-Hash $cache $archiveDigest) {$archive=$cache}
            else {
                $archive=Join-Path $stage 'nvidia.7z'
                & (Join-Path ([Environment]::SystemDirectory) 'curl.exe') --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --retry 2 --max-time 600 --output $archive $packageUrl
                if($LASTEXITCODE){throw 'The NVIDIA neural rendering package download failed.'}
            }
        }
        if(!(Check-Hash $archive $archiveDigest)){throw 'The NVIDIA package failed SHA-256 verification.'}
        & (Join-Path ([Environment]::SystemDirectory) 'tar.exe') -xf $archive -C $stage lib/Windows_x86_64/rel/nvngx_dlssnr.dll LICENSE.txt
        if($LASTEXITCODE){throw 'Could not unpack the NVIDIA model resource.'}
        $dll=Join-Path $stage 'lib\Windows_x86_64\rel\nvngx_dlssnr.dll'
        $license=Join-Path $stage 'LICENSE.txt'
        if(!(Check-Hash $dll $runtimeDigest) -or !(Check-Hash $license $licenseDigest)){throw 'NVIDIA package files failed checksum verification.'}
        $signer=Get-AuthenticodeSignature -LiteralPath $dll
        if($signer.Status -ne 'Valid' -or $signer.SignerCertificate.Subject -notmatch '(?:^|, )O=NVIDIA Corporation(?:,|$)'){throw 'Could not verify the NVIDIA publisher signature.'}
        $model=Join-Path $stage 'model-directory'
        Add-Type -Path (Join-Path $PSScriptRoot 'OpenNrModelData.cs') -ReferencedAssemblies System.Core,System.Web.Extensions
        [OpenNrModelData]::WriteDirectory($dll,$model)
        [IO.File]::Copy($license,(Join-Path $model 'NVIDIA-LICENSE.txt'))
        if(!(Test-Complete $model)){throw 'The prepared model did not pass verification.'}
        try { [IO.Directory]::Move($model,$dest) } catch [IO.IOException] { if(!(Test-Complete $dest)){throw} }
    }
    @{ok=$true;message='OpenDLSS-NR model is ready.';modelDirectory=$dest} | ConvertTo-Json -Compress
} catch {
    @{ok=$false;message=$_.Exception.Message} | ConvertTo-Json -Compress
    $exitCode=1
} finally {
    if($stage -and [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($stage)) -eq $parent -and [IO.Path]::GetFileName($stage) -eq $id) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}
exit $exitCode
