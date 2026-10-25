# Builds the self-contained single-file exe and the release zip.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'publish'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $root 'src\Cs2DropChecker.App\Cs2DropChecker.App.csproj') -p:PublishProfile=win-x64 -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $out 'cs2-drop-checker.exe'
Copy-Item $exe (Join-Path $root 'cs2-drop-checker.exe') -Force

$stage = Join-Path $out 'zip'
New-Item $stage -ItemType Directory -Force | Out-Null
Copy-Item $exe, (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') $stage
$zip = Join-Path $root 'cs2-drop-checker.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip

Get-Item (Join-Path $root 'cs2-drop-checker.exe'), $zip | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
