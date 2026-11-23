[CmdletBinding()]
param([Parameter(Mandatory)][string]$RuntimePath)
$ErrorActionPreference='Stop'
$parent=Join-Path $env:LOCALAPPDATA 'P5Cast\runtimes'
$destination=Join-Path $parent 'fruc'
$stage=Join-Path $parent ('fruc-setup-'+[guid]::NewGuid().ToString('N'))
$cudaName='cudart64_110.dll'
$cudaHash='BA5C2FB526C4EE4BB218CEB3FA5E8BFDE89CE474F38711FDCCE802549BF9FC6F'
$cudaZipHash='988CC9E7D3785D4B1975521F312C57C6814CBF15E73A2B7941D961835F2A945E'
$cudaUrl='https://developer.download.nvidia.com/compute/cuda/redist/cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-11.8.89-archive.zip'

function Test-FrucImage([string]$path) {
    if((Get-Item -LiteralPath $path).Length -gt 64MB){return $false}
    $bytes=[IO.File]::ReadAllBytes($path)
    if($bytes.Length -lt 64 -or [BitConverter]::ToUInt16($bytes,0) -ne 0x5A4D){return $false}
    $pe=[BitConverter]::ToInt32($bytes,60)
    if($pe -lt 0 -or $pe -gt $bytes.Length-24 -or [BitConverter]::ToUInt32($bytes,$pe) -ne 0x4550 -or [BitConverter]::ToUInt16($bytes,$pe+4) -ne 0x8664){return $false}
    $symbols=[Text.Encoding]::ASCII.GetString($bytes)
    foreach($export in 'NvOFFRUCCreate','NvOFFRUCRegisterResource','NvOFFRUCProcess','NvOFFRUCUnregisterResource','NvOFFRUCDestroy'){
        if(!$symbols.Contains($export+[char]0)){return $false}
    }
    $sig=Get-AuthenticodeSignature -LiteralPath $path
    return ($sig.Status -eq 'Valid' -and $sig.SignerCertificate.Subject -match '(?:^|,\s*)O=NVIDIA Corporation(?:,|$)')
}
function Publish-File([string]$file) {
    $target=Join-Path $destination ([IO.Path]::GetFileName($file))
    if([IO.File]::Exists($target)){
        if((Get-FileHash -LiteralPath $target).Hash -eq (Get-FileHash -LiteralPath $file).Hash){return}
        [IO.File]::Replace($file,$target,(Join-Path $stage ('old-'+[IO.Path]::GetFileName($file))))
    } else {[IO.File]::Move($file,$target)}
}
$code=0
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $source=(Resolve-Path -LiteralPath $RuntimePath).Path
    [void][IO.Directory]::CreateDirectory($stage)
    $runtime=Join-Path $stage 'NvOFFRUC.dll'
    if([IO.Path]::GetExtension($source) -eq '.zip'){
        $archive=[IO.Compression.ZipFile]::OpenRead($source)
        try {
            foreach($entry in $archive.Entries){
                if($entry.Name -ne 'NvOFFRUC.dll' -or $entry.Length -gt 64MB){continue}
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$runtime,$true)
                if(Test-FrucImage $runtime){break}
            }
        } finally {$archive.Dispose()}
    } elseif([IO.Path]::GetExtension($source) -eq '.dll'){[IO.File]::Copy($source,$runtime)}
    if(![IO.File]::Exists($runtime) -or !(Test-FrucImage $runtime)){
        throw 'Choose the NVIDIA Optical Flow SDK ZIP or its NVIDIA-signed x64 NvOFFRUC.dll.'
    }
    $cuda=Join-Path $stage $cudaName
    $existing=Join-Path $destination $cudaName
    if(![IO.File]::Exists($existing) -or (Get-FileHash -LiteralPath $existing).Hash -ne $cudaHash){
        $zip=Join-Path $stage 'cuda.zip'
        & (Join-Path ([Environment]::SystemDirectory) 'curl.exe') --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --retry 2 --output $zip $cudaUrl
        if($LASTEXITCODE -or (Get-FileHash -LiteralPath $zip).Hash -ne $cudaZipHash){throw 'Could not verify the NVIDIA CUDA 11.8 runtime download.'}
        $bundle=[IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $entry=$bundle.GetEntry('cuda_cudart-windows-x86_64-11.8.89-archive/bin/'+$cudaName)
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$cuda)
        } finally {$bundle.Dispose()}
        if((Get-FileHash -LiteralPath $cuda).Hash -ne $cudaHash){throw 'CUDA runtime checksum mismatch.'}
    }
    [void][IO.Directory]::CreateDirectory($destination)
    $lock=[IO.File]::Open((Join-Path $parent 'fruc-install.lock'),'OpenOrCreate','ReadWrite','None')
    try {
        if([IO.File]::Exists($cuda)){Publish-File $cuda}
        Publish-File $runtime
    } finally {$lock.Dispose()}
    @{ok=$true;message='NVIDIA FRUC runtime installed.'} | ConvertTo-Json -Compress
} catch {
    @{ok=$false;message=$_.Exception.Message} | ConvertTo-Json -Compress
    $code=1
} finally {
    $resolved=[IO.Path]::GetFullPath($stage)
    if([IO.Path]::GetDirectoryName($resolved) -eq [IO.Path]::GetFullPath($parent) -and [IO.Directory]::Exists($resolved)){
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
exit $code
