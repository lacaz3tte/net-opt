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

Get-Item $exe | ForEach-Object {
    Write-Host ("Built {0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB))
}

Write-Host "Native AOT is not used: WPF is not compatible with Native AOT. Self-contained single-file is the supported distribution mode."
