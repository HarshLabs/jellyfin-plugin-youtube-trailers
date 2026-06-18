#!/usr/bin/env bash
set -euo pipefail

PROJECT="Jellyfin.Plugin.YouTubeTrailers"
VERSION="1.1.0.0"
PLUGIN_GUID="00e99003-cf35-4a65-bf44-35104dfeb76a"

REPO_ROOT="$(cd "$(dirname "$0")" && pwd)"
PROJ_DIR="$REPO_ROOT/$PROJECT"
OUT_DIR="$REPO_ROOT/out"
PLUGINS_DIR="$HOME/Library/Application Support/jellyfin/plugins"
INSTALL_DIR="$PLUGINS_DIR/${PROJECT}_${VERSION}"

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

dotnet publish "$PROJ_DIR/$PROJECT.csproj" \
  -c Release -f net9.0 \
  -o "$OUT_DIR" \
  --nologo

TS=$(date -u +%Y-%m-%dT%H:%M:%S.0000000Z)
cat > "$OUT_DIR/meta.json" <<EOF
{
  "guid": "$PLUGIN_GUID",
  "name": "YouTube Trailers",
  "description": "Server-side yt-dlp resolution + ffmpeg stream-copy remux of YouTube trailers to AVPlayer-native fMP4 HLS.",
  "owner": "local",
  "category": "General",
  "overview": "Native HLS YouTube trailers for AVPlayer-based clients",
  "targetAbi": "10.11.0.0",
  "version": "$VERSION",
  "changelog": "1.0.0 — server-side YouTube trailer resolution (yt-dlp + ffmpeg) served as AVPlayer-native HLS for in-app + Top Shelf; config page with cache management, prune task, and yt-dlp arguments/version.",
  "timestamp": "$TS",
  "status": 0,
  "autoUpdate": false,
  "imagePath": null,
  "assemblies": ["${PROJECT}.dll"]
}
EOF

mkdir -p "$INSTALL_DIR"
cp "$OUT_DIR/${PROJECT}.dll" "$INSTALL_DIR/"
cp "$OUT_DIR/meta.json" "$INSTALL_DIR/"
[ -f "$OUT_DIR/${PROJECT}.pdb" ] && cp "$OUT_DIR/${PROJECT}.pdb" "$INSTALL_DIR/" || true

echo "Installed to: $INSTALL_DIR"
ls -la "$INSTALL_DIR"
