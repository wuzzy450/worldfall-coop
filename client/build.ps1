# build.ps1 - builds Coopfall.dll (Release) and installs it into WorldBox's mods folder.
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1 [-GameDir "D:\Steam\steamapps\common\worldbox"] [-NoInstall]
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\worldbox",
    [switch]$NoInstall
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $here 'Coopfall\Coopfall.csproj'

$dotnet = Join-Path $here '..\tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

& $dotnet build $proj -c Release -nologo -v minimal "-p:GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$dll = Join-Path $here 'Coopfall\bin\Release\Coopfall.dll'
Write-Output ("built " + $dll)
if (-not $NoInstall) {
    $mods = Join-Path $GameDir 'worldbox_Data\StreamingAssets\mods'
    if (-not (Test-Path $mods)) { New-Item -ItemType Directory -Force $mods | Out-Null }
    Copy-Item $dll (Join-Path $mods 'Coopfall.dll') -Force
    Write-Output ("installed to " + (Join-Path $mods 'Coopfall.dll'))
}
