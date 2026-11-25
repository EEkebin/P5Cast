[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Archive,
    [string]$Compiler='ISCC.exe'
)
$ErrorActionPreference='Stop'
$zip=Get-Item -LiteralPath $Archive
$expected=((Get-Content -LiteralPath ($zip.FullName+'.sha256') -Raw).Trim() -split '\s+')[0]
if ((Get-FileHash -LiteralPath $zip.FullName).Hash -ne $expected) { throw 'Portable ZIP checksum mismatch.' }
$compilerPath=(Get-Command $Compiler -CommandType Application -ErrorAction Stop).Source
$work=[IO.Directory]::CreateDirectory((Join-Path $zip.DirectoryName ('setup-'+[guid]::NewGuid().ToString('N')))).FullName
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zip.FullName,$work)
    $payload=Join-Path $work 'P5Cast'
    $manifest=Get-Content -LiteralPath (Join-Path $payload 'payload.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $version=$manifest.version
    if ($version -notmatch '^\d+\.\d+\.\d+$' -or $zip.Name -ne "P5Cast-$version-windows-x64.zip") { throw 'Unexpected release version or archive name.' }
    $hashes=@{}
    foreach ($entry in $manifest.files) {
        $hashes.Add($entry.path.Replace('/','\'),$entry.sha256)
    }
    foreach ($file in Get-ChildItem -LiteralPath $payload -File -Force -Recurse) {
        $name=$file.FullName.Substring($payload.Length+1)
        if ($name -eq 'payload.json') { continue }
        if (!$hashes.ContainsKey($name) -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne $hashes[$name]) {
            throw "Unexpected or damaged package file: $name"
        }
        $hashes.Remove($name)
    }
    if ($hashes.Count) { throw 'The portable package is missing files.' }
    & $compilerPath /Qp "/DPayloadDir=$payload" "/DReleaseVersion=$version" "/O$work" (Join-Path $PSScriptRoot 'P5Cast.iss') | Out-Host
    if ($LASTEXITCODE) { throw 'Setup compilation failed.' }
    $name="P5Cast-$version-windows-x64-setup.exe"
    $setup=Join-Path $work $name
    $hash=(Get-FileHash -LiteralPath $setup).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$setup.sha256","$hash  $name"+[Environment]::NewLine)
    foreach ($path in $setup,"$setup.sha256") {
        $dest=Join-Path $zip.DirectoryName ([IO.Path]::GetFileName($path))
        if ([IO.File]::Exists($dest)) { [IO.File]::Replace($path,$dest,[System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($path,$dest) }
    }
    Get-Item -LiteralPath (Join-Path $zip.DirectoryName $name)
} finally {
    if ([IO.Path]::GetDirectoryName($work) -eq $zip.DirectoryName -and [IO.Path]::GetFileName($work) -match '^setup-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}
