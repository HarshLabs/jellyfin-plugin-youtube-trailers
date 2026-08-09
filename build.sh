#!/usr/bin/env bash
set -euo pipefail

# Local dev build + hot-deploy.
#
# The plugin ships for two Jellyfin ABIs (see abi.env). This script figures out
# which one the locally installed server needs and deploys that build, so you
# don't have to think about it. Override with:  ./build.sh net10.0

PROJECT="Jellyfin.Plugin.YouTubeTrailers"
VERSION="1.2.3.2"
PLUGIN_GUID="00e99003-cf35-4a65-bf44-35104dfeb76a"

REPO_ROOT="$(cd "$(dirname "$0")" && pwd)"
PROJ_DIR="$REPO_ROOT/$PROJECT"
OUT_DIR="$REPO_ROOT/out"
PLUGINS_DIR="$HOME/Library/Application Support/jellyfin/plugins"
INSTALL_DIR="$PLUGINS_DIR/${PROJECT}_${VERSION}"

# Refuse to deploy over a running server.
#
# Jellyfin memory-maps plugin assemblies. Replacing or deleting one while the
# server is live leaves the process reading pages that no longer back the file,
# so any method it JIT-compiles *after* that point gets garbage IL. It surfaces
# as BadImageFormatException / InvalidProgramException / "Bad binary signature"
# from random endpoints — and, worst of all, only from the endpoints that hadn't
# been hit yet, which makes it look like a code bug in whatever you just edited.
#
# Note that quitting the Jellyfin.app wrapper does NOT stop the server child
# process, so check for the actual process, not the app.
# Three shapes: the bare server binary, the .app's wrapper child, and a
# source run via `dotnet .../Jellyfin.Server.dll`.
if pgrep -x jellyfin >/dev/null 2>&1 \
  || pgrep -f "Jellyfin Server" >/dev/null 2>&1 \
  || pgrep -f "Jellyfin.Server.dll" >/dev/null 2>&1; then
  echo "ERROR: Jellyfin is still running — refusing to replace plugin files underneath it." >&2
  echo "       Deploying now would corrupt the running process's mapped assembly." >&2
  echo "       Stop it first:  osascript -e 'quit app \"Jellyfin\"'; pkill -x jellyfin; pkill -f 'Jellyfin Server'" >&2
  # List whichever shape tripped the guard; each may legitimately match nothing,
  # and under set -e a bare failing pgrep would abort before the exit message.
  { pgrep -xl jellyfin || true; pgrep -fl "Jellyfin Server" || true; pgrep -fl "Jellyfin.Server.dll" || true; } >&2
  exit 1
fi

# ── Which ABI does the installed server need? ────────────────────────────────
# Jellyfin 12 runs on .NET 10 and ships a different Controller assembly than
# 10.11; we build against both and pick by the installed server's major version.
detect_framework() {
  local controller="/Applications/Jellyfin.app/Contents/MacOS/MediaBrowser.Controller.dll"
  if [ -f "$controller" ]; then
    if strings "$controller" 2>/dev/null | grep -qE '^12\.[0-9]+\.[0-9]+'; then
      echo "net10.0"; return
    fi
  fi
  echo "net9.0"
}

FRAMEWORK="${1:-$(detect_framework)}"
case "$FRAMEWORK" in
  net9.0)  ABI="10.11.0.0" ;;
  net10.0) ABI="12.0.0.0" ;;
  *) echo "ERROR: unknown framework '$FRAMEWORK' (expected net9.0 or net10.0)" >&2; exit 1 ;;
esac
echo "Building for $FRAMEWORK (targetAbi $ABI)"

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

dotnet publish "$PROJ_DIR/$PROJECT.csproj" \
  -c Release -f "$FRAMEWORK" \
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
  "targetAbi": "$ABI",
  "version": "$VERSION",
  "changelog": "Local dev build ($FRAMEWORK).",
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
