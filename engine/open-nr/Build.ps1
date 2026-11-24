[CmdletBinding()]
param([string]$OutDir=(Join-Path $PSScriptRoot '..\bin'))
$ErrorActionPreference='Stop'
$OutDir=[IO.Path]::GetFullPath($OutDir)
$cache=Join-Path $env:LOCALAPPDATA 'P5Cast\build\opendlss-nr'
[IO.Directory]::CreateDirectory($cache)|Out-Null
# Source, loader and compiler are pinned independently. No model data is distributed.
$revision='9d08f4184bbcb9d858e2fb7a7834ec0837a9d2f1'
$sources=@(
    @('up',"https://codeload.github.com/maanHimself/OpenDLSS-NR/zip/$revision",'4465A28AC4DDB19C2A9A18D848951FD657E377FF16B039F36E95960C6BBE1EAD'),
    @('headers','https://codeload.github.com/KhronosGroup/Vulkan-Headers/zip/6802bb4733b63ed5efd3adb308a6c885ef180ea1','3760505FFBD3589DE77E1B9F5BA11EAAC13D3C86869588E19F3DF3E0FAC7E69C'),
    @('volk','https://codeload.github.com/zeux/volk/zip/776893306c5d3b22b6185b5d4a258b81d94572bf','15C2783F46DB3EBD1F86CA9F9C5E3344C450533CCF3262334D9589122872CDA1'),
    @('glslang','https://github.com/KhronosGroup/glslang/releases/download/16.6.0/glslang-16.6.0-windows-x86_64-release.zip','82BF434E69B9BB4829DE7E2B4BC2C5E7A7861E53D66CF75E5CC70F5F694A8D9B')
)
$mutex=New-Object Threading.Mutex($false,'Local\P5Cast-OpenNR-build')
$locked=$false
try {
    try {$locked=$mutex.WaitOne([TimeSpan]::FromMinutes(10))} catch [Threading.AbandonedMutexException] {$locked=$true}
    if(!$locked){throw 'Timed out waiting for the OpenDLSS-NR build cache'}
    $source=Join-Path $cache $revision
    foreach($item in $sources){
        $name,$url,$digest=$item
        $archive=Join-Path $cache "$name.zip"
        $destination=switch($name){up {$source} headers {"$source\tools\Vulkan-Headers"} default {"$source\tools\$name"}}
        $marker=Join-Path $destination ".$digest"
        if(Test-Path -LiteralPath $marker){continue}
        if(!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash -ne $digest){
            & curl.exe -fsSL --retry 2 --output $archive $url
            if($LASTEXITCODE -or (Get-FileHash -LiteralPath $archive).Hash -ne $digest){throw "Could not verify $name"}
        }
        [IO.Directory]::CreateDirectory($destination)|Out-Null
        $strip=if($name -eq 'glslang'){0}else{1}
        & tar.exe -xf $archive -C $destination "--strip-components=$strip"
        if($LASTEXITCODE){throw "Could not unpack $name"}
        [IO.File]::WriteAllText($marker,$digest)
    }
    $nativeAssets=Join-Path $source 'build'
    $kernelMarker=Join-Path $nativeAssets '.kernels-complete'
    if(!(Test-Path -LiteralPath $kernelMarker)){
        & "$source\scripts\build_shaders.ps1"
        if($LASTEXITCODE){throw 'OpenDLSS-NR inference kernels failed to build'}
        [IO.File]::WriteAllText($kernelMarker,$revision)
    }
    $shaders=Join-Path $nativeAssets 'shaders'
    foreach($pass in 'prepare','compose'){
        & "$source\tools\glslang\bin\glslang.exe" -V --target-env vulkan1.3 "-I$source\demo\shaders" "$PSScriptRoot\$pass.comp" -o "$shaders\video-$pass.spv"
        if($LASTEXITCODE){throw "Could not compile OpenDLSS-NR video $pass"}
    }
    . "$PSScriptRoot\..\nvidia\Use-Compiler.ps1"
    $obj=Join-Path $nativeAssets 'p5cast-obj'
    [IO.Directory]::CreateDirectory($obj)|Out-Null
    [IO.Directory]::CreateDirectory($OutDir)|Out-Null
    Push-Location $obj
    try{
        # Apply the Windows Unicode, checksum comparison and GPU resource lifetime
        # fixes to a fresh copy. All translation units must share the patched headers.
        $patched=Join-Path $obj 'src'
        [IO.Directory]::CreateDirectory($patched)|Out-Null
        Copy-Item "$source\src\*.h","$source\src\*.cpp" -Destination $patched -Force
        & git.exe apply --no-index --directory=src --ignore-space-change "$PSScriptRoot\upstream.patch"
        if($LASTEXITCODE){throw 'Could not apply P5Cast OpenDLSS-NR integration fixes'}
        $inputs=foreach($unit in 'vk_context','kernels','nr_graph','nr_model','reference'){Join-Path $patched "$unit.cpp"}
        & cl.exe /nologo /LD /O2 /MT /std:c++20 /EHsc /utf-8 /DNOMINMAX /DWIN32_LEAN_AND_MEAN /DVK_ENABLE_BETA_EXTENSIONS /D_CRT_SECURE_NO_WARNINGS "/I$patched" "/I$source\tools\volk" "/I$source\tools\Vulkan-Headers\include" @inputs "$source\tools\volk\volk.c" "$PSScriptRoot\OpenNr.cpp" /link /BREPRO /NODEFAULTLIB:oldnames.lib /MANIFEST:NO /OUT:P5Cast.OpenNr.dll
        if($LASTEXITCODE){throw 'OpenDLSS-NR Vulkan bridge failed to compile'}
        Copy-Item -LiteralPath 'P5Cast.OpenNr.dll' -Destination $OutDir -Force
    }finally{Pop-Location}
    $bundle=Join-Path $OutDir 'open-nr'
    foreach($dir in 'shaders','ptx','licenses'){[IO.Directory]::CreateDirectory((Join-Path $bundle $dir))|Out-Null}
    Copy-Item "$shaders\*.spv" "$bundle\shaders" -Force
    Copy-Item "$nativeAssets\ptx\*.ptx" "$bundle\ptx" -Force
    foreach($notice in 'LICENSE','NOTICE'){
        Copy-Item -LiteralPath "$source\$notice" -Destination "$bundle\licenses\OpenDLSS-NR-$notice.txt" -Force
    }
    Copy-Item -LiteralPath "$source\tools\volk\LICENSE.md" -Destination "$bundle\licenses\volk.txt" -Force
    Copy-Item -LiteralPath "$source\tools\Vulkan-Headers\LICENSE.md" -Destination "$bundle\licenses\Vulkan-Headers.txt" -Force
    foreach($license in 'MIT','Apache-2.0'){
        Copy-Item -LiteralPath "$source\tools\Vulkan-Headers\LICENSES\$license.txt" -Destination "$bundle\licenses\Vulkan-Headers-$license.txt" -Force
    }
}finally{
    if($locked){$mutex.ReleaseMutex()}
    $mutex.Dispose()
}
