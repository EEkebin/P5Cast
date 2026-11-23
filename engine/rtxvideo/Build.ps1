[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\bin'),
    [switch]$Verify,
    [ValidateSet('d3d11','d3d12')][string]$VerifyBackend='d3d11'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$archiveSHA = 'ABF4F34E2B5A618E355B0D5A0365D8ECC3DB4396E756E4C850A867E1AE2ED69E'
$dllSHA = 'C3D88EEA5FF7A548EDEFA66414CF6E77464D0947277C904F324DD23ABF58A1ED'
$cache = Join-Path $env:LOCALAPPDATA 'Cadrivect\build\rtx-video'
$archive = Join-Path $cache 'RTX_Video_SDK_v1.1.0.zip'
$url = 'https://api.ngc.nvidia.com/v2/models/nvidia/multimedia/dlpp/versions/1.5/files/RTX_Video_SDK_v1.1.0.zip'
$runId = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $cache $runId
[IO.Directory]::CreateDirectory($stage) | Out-Null
$out = [IO.Directory]::CreateDirectory($OutDir).FullName
$previousProtocols = [Net.ServicePointManager]::SecurityProtocol
try {
    if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
        . (Join-Path $PSScriptRoot '..\nvidia\Use-Compiler.ps1')
    }
    if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash -ne $archiveSHA) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $response = Invoke-WebRequest -UseBasicParsing -Method Head $url
        $location = [Uri](@($response.Headers['Location'])[0])
        if ($location.Scheme -ne 'https' -or $location.Host -ne 'xfiles.ngc.nvidia.com') {
            throw 'NVIDIA NGC returned an unexpected download location.'
        }
        $download = Join-Path $stage 'download.zip'
        $web = New-Object Net.WebClient
        try { $web.DownloadFile($location, $download) } finally { $web.Dispose() }
        if ((Get-FileHash -LiteralPath $download).Hash -ne $archiveSHA) { throw 'NVIDIA RTX Video SDK archive SHA-256 mismatch.' }
        [IO.File]::Copy($download, $archive, $true)
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $sdk = Join-Path $stage 'sdk'
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $sdk)
    $runtime = Join-Path $sdk 'bin\Windows\x64\rel\nvngx_vsr.dll'
    $signature = Get-AuthenticodeSignature -LiteralPath $runtime
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=NVIDIA Corporation,' -or
        (Get-FileHash -LiteralPath $runtime).Hash -ne $dllSHA) { throw 'NVIDIA RTX Video runtime verification failed.' }

    Push-Location $stage
    try {
        & cl.exe /nologo /MT /O2 /EHsc /LD "/I$sdk\include" "$PSScriptRoot\RtxVideo.cpp" /link /BREPRO /MANIFEST:NO /NODEFAULTLIB:oldnames.lib /OUT:Cadrivect.RtxVideo.dll "$sdk\lib\Windows\x64\nvsdk_ngx_s.lib" advapi32.lib wintrust.lib crypt32.lib user32.lib
        if ($LASTEXITCODE -ne 0) { throw 'The RTX Video bridge did not compile.' }
        Copy-Item -LiteralPath $runtime -Destination $stage
        if ($Verify) {
            if ($VerifyBackend -eq 'd3d12') { Copy-Item -LiteralPath (Join-Path $out 'Cadrivect.Ngx.dll') -Destination $stage }
            $refs = foreach ($lib in @('SharpDX', 'SharpDX.Direct3D11', 'SharpDX.DXGI')) {
                $path = Join-Path $out "$lib.dll"
                if (!(Test-Path -LiteralPath $path)) { throw "Build the managed player first to stage $lib.dll." }
                Copy-Item -LiteralPath $path -Destination $stage
                "/reference:$path"
            }
            $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
            & $csc /nologo /platform:x64 /out:Verify.exe $refs "$PSScriptRoot\Verify.cs" "$PSScriptRoot\RtxVideo.cs" "$PSScriptRoot\..\D3D12Effects.cs"
            if ($LASTEXITCODE -ne 0) { throw 'The RTX Video GPU test did not compile.' }
            & .\Verify.exe $VerifyBackend
            if ($LASTEXITCODE -ne 0) { throw 'The RTX Video GPU output failed validation.' }
        }
        Copy-Item -LiteralPath '.\Cadrivect.RtxVideo.dll', $runtime, "$sdk\NVIDIA_RTX_Video_SDK_License.pdf" -Destination $out -Force
    } finally { Pop-Location }
} finally {
    [Net.ServicePointManager]::SecurityProtocol = $previousProtocols
    $resolved = [IO.Path]::GetFullPath($stage)
    if ([IO.Path]::GetDirectoryName($resolved) -eq [IO.Path]::GetFullPath($cache) -and [IO.Path]::GetFileName($resolved) -eq $runId) {
        # Some SDK/driver processes briefly retain the working directory after a successful GPU run.
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
}
