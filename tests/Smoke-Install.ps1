[CmdletBinding()]
param([string]$PackageRoot)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'Install.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw $errors[0]}
$fn=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Install-Payload'},$false)
. ([scriptblock]::Create($fn.Extent.Text))
$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$fixture=Join-Path $temp ('P5Cast-install-test-'+[guid]::NewGuid().ToString('N'))
$src=Join-Path $fixture 'Extracted Package'
$app=Join-Path $fixture 'Programs\P5Cast'
$menu=Join-Path $fixture 'Test Start Menu\P5Cast'
$shell=New-Object -ComObject WScript.Shell
function ShortcutTarget { $shell.CreateShortcut((Join-Path $menu 'P5Cast.lnk')).TargetPath }
function Manifest($version) {
    $items=@(foreach($name in $files){@{path=$name;sha256=(Get-FileHash -LiteralPath (Join-Path $src $name)).Hash}})
    [IO.File]::WriteAllText((Join-Path $src 'payload.json'),(@{version=$version;files=$items}|ConvertTo-Json -Depth 4))
}
try {
    $files=@('P5Cast.exe','app\Main.xaml','engine\bin\CadrivectPlayer.exe','engine\bin\direct\CadrivectDirect.exe','engine\bin\direct\SDL2.dll')
    foreach($name in $files){
        $file=Join-Path $src $name; [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file)); [IO.File]::WriteAllText($file,'original')
    }
    Manifest '1.2.3'
    $first=Install-Payload $src $app $menu
    if((ShortcutTarget) -ne $first -or (Install-Payload $src $app $menu) -ne $first){throw 'Fresh install or same-version retry failed.'}
    $busy=[IO.File]::Open((Join-Path (Split-Path $first) $files[-1]),'Open','Read','None')
    try {
        $replacement=Install-Payload $src $app $menu
        if($replacement -eq $first -or (ShortcutTarget) -ne $replacement){throw 'An unreadable installed DLL blocked side-by-side repair.'}
    } finally {$busy.Dispose()}
    $first=$replacement
    Manifest '1.2.4'
    $dll=Join-Path $src $files[-1]
    [IO.File]::WriteAllText($dll,'changed')
    $failed=$false
    try {$null=Install-Payload $src $app $menu} catch {$failed=$true}
    if(!$failed -or (ShortcutTarget) -ne $first -or (Test-Path (Join-Path $app 'versions\1.2.4'))){throw 'A damaged update replaced the previous install.'}
    [IO.File]::Delete($dll);$failed=$false
    try {$null=Install-Payload $src $app $menu} catch {$failed=$true}
    if(!$failed -or (ShortcutTarget) -ne $first){throw 'A missing file replaced the active installation.'}
    [IO.File]::WriteAllText($dll,'original')
    $second=Install-Payload $src $app $menu
    [IO.File]::WriteAllText((Join-Path (Split-Path $second) $files[-1]),'broken')
    $repaired=Install-Payload $src $app $menu
    if($repaired -eq $second -or (ShortcutTarget) -ne $repaired -or (Install-Payload $src $app $menu) -ne $repaired){throw 'Same-version repair failed.'}
    if(@(Get-ChildItem (Join-Path $app 'versions') -Directory -Filter '.install-*').Count){throw 'A failed installation left a staging directory.'}
    if ($PackageRoot) {
        $checkout=Join-Path $fixture 'package-fixture'
        [void][IO.Directory]::CreateDirectory($checkout)
        Copy-Item -LiteralPath (Join-Path $repo 'Package.ps1') -Destination $checkout
        $release=Get-Content (Join-Path $PackageRoot 'payload.json') -Raw | ConvertFrom-Json
        foreach ($entry in $release.files) {
            $path=Join-Path $checkout $entry.path
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
            [IO.File]::WriteAllText($path,'fixture')
        }
        $pack=Join-Path $checkout 'Package.ps1'
        $zip=(& $pack -Version '1.2.3').FullName
        $oldHash=(Get-FileHash $zip).Hash
        [IO.File]::WriteAllText((Join-Path $checkout 'P5Cast.exe'),'updated fixture')
        $locked=[IO.File]::Open("$zip.sha256",'Open','Read','Read')
        try {
            $failed=$false
            try {$null=& $pack -Version '1.2.3'} catch {$failed=$true}
        } finally {$locked.Dispose()}
        if(!$failed -or (Get-FileHash $zip).Hash -ne $oldHash -or (Get-Content "$zip.sha256" -Raw) -notlike "$oldHash *"){throw 'Checksum publication failure damaged the previous release.'}
        $null=& $pack -Version '1.2.3'
        if((Get-FileHash $zip).Hash -eq $oldHash){throw 'Retry failed to publish a new archive.'}
        'Passed archive/checksum rollback and retry.'
    }
    'Passed fresh install, retry, interrupted update, missing and corrupt files, same-version repair and shortcut targeting.'
} finally {
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($fixture)) -eq $temp -and (Split-Path $fixture -Leaf) -match '^P5Cast-install-test-[0-9a-f]{32}$' -and [IO.Directory]::Exists($fixture)){
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
