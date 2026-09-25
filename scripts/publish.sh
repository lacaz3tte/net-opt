#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/dist"
mkdir -p "$OUT"

echo "Publishing Network Optimizer (win-x64 self-contained single-file)..."
dotnet publish "$ROOT/src/NetworkOptimizer.App/NetworkOptimizer.App.csproj" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=embedded \
  -p:PublishTrimmed=false \
  -p:EnableWindowsTargeting=true \
  -o "$OUT"

if [[ ! -f "$OUT/NetworkOptimizer.exe" ]]; then
  echo "Publish succeeded but NetworkOptimizer.exe was not found in dist/" >&2
  exit 1
fi

if [[ -d "$ROOT/third_party/zapret" ]]; then
  rm -rf "$OUT/zapret"
  cp -a "$ROOT/third_party/zapret" "$OUT/zapret"
  mkdir -p "$OUT/lists"
  cp -f "$ROOT/lists/youtube-discord.txt" "$OUT/lists/youtube-discord.txt"
  cp -f "$ROOT/lists/youtube-discord.txt" "$OUT/zapret/files/youtube-discord.txt"
fi

ls -lh "$OUT/NetworkOptimizer.exe"
echo "Native AOT is not used: WPF is not compatible with Native AOT."
