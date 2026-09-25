$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $out | Out-Null

Write-Host "Publishing Network Optimizer (win-x64 self-contained single-file)..."
dotnet publish (Join-Path $root "src/NetworkOptimizer.App/NetworkOptimizer.App.csproj") `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=embedded `
  -p:PublishTrimmed=false `
  -p:EnableWindowsTargeting=true `
  -o $out

$exe = Join-Path $out "NetworkOptimizer.exe"
if (-not (Test-Path $exe)) {
    throw "Publish succeeded but NetworkOptimizer.exe was not found in dist/"
}

$zapretSrc = Join-Path $root "third_party\zapret"
$zapretDst = Join-Path $out "zapret"
if (Test-Path $zapretSrc) {
    if (Test-Path $zapretDst) { Remove-Item -Recurse -Force $zapretDst }
    Copy-Item -Recurse -Force $zapretSrc $zapretDst
    New-Item -ItemType Directory -Force -Path (Join-Path $out "lists") | Out-Null
    Copy-Item -Force (Join-Path $root "lists\youtube-discord.txt") (Join-Path $out "lists\youtube-discord.txt")
    Copy-Item -Force (Join-Path $root "lists\youtube-discord.txt") (Join-Path $zapretDst "files\youtube-discord.txt")
}

Get-Item $exe | ForEach-Object {
    Write-Host ("Built {0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB))
}

Write-Host "Native AOT is not used: WPF is not compatible with Native AOT. Self-contained single-file is the supported distribution mode."
