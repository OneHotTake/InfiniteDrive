#!/bin/bash
# emby-reset.sh — Clean build, wipe state, redeploy DLL, restart on port 8096

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EMBY_BIN="$(dirname "$(readlink -f "$0")")/../emby-beta/opt/emby-server/bin/emby-server"
DATA_DIR="$HOME/emby-dev-data"
LOG_FILE="$DATA_DIR/logs/embyserver.txt"
MEDIA_DIR="/media/embystreams"
export LD_LIBRARY_PATH=/usr/lib/x86_64-linux-gnu:$LD_LIBRARY_PATH 
export PATH="$PATH:$HOME/.dotnet"

# ── 1. Stop any running Emby ──────────────────────────────────────────────────
echo "[1/5] Stopping Emby..."
pkill -TERM -f "emby-server" 2>/dev/null || true
for i in $(seq 1 10); do
    sleep 1
    pgrep -f "emby-server" > /dev/null 2>&1 || { echo "      Stopped."; break; }
    [ "$i" -eq 10 ] && { pkill -KILL -f "emby-server" 2>/dev/null || true; echo "      Force-killed."; }
done

# ── 2. Clear emby-dev-data (everything except plugins dir) ───────────────────
echo "[2/5] Clearing emby-dev-data..."
rm -rf \
    "$DATA_DIR/cache" \
    "$DATA_DIR/config" \
    "$DATA_DIR/data" \
    "$DATA_DIR/logs" \
    "$DATA_DIR/metadata" \
    "$DATA_DIR/transcoding-temp"
mkdir -p \
    "$DATA_DIR/cache" \
    "$DATA_DIR/config" \
    "$DATA_DIR/data" \
    "$DATA_DIR/logs" \
    "$DATA_DIR/metadata" \
    "$DATA_DIR/plugins" \
    "$DATA_DIR/transcoding-temp"
echo "      Done."

# ── 3. Clear .strm files from media dirs ─────────────────────────────────────
echo "[3/5] Clearing .strm files from $MEDIA_DIR..."
find "$MEDIA_DIR" -name "*.strm" -delete 2>/dev/null || true
find "$MEDIA_DIR" -name "*.nfo" -delete 2>/dev/null || true
find "$MEDIA_DIR" -mindepth 2 -type d -empty -delete 2>/dev/null || true
echo "      Done."

# ── 4. Build and deploy DLL ───────────────────────────────────────────────────
echo "[4/5] Building EmbyStreams..."
cd "$SCRIPT_DIR"
dotnet publish -c Release || { echo "BUILD FAILED — aborting."; exit 1; }

DLL="$SCRIPT_DIR/bin/Release/net8.0/publish/EmbyStreams.dll"
if [ ! -f "$DLL" ]; then
    echo "ERROR: DLL not found at $DLL"; exit 1
fi
cp "$DLL" "$DATA_DIR/plugins/EmbyStreams.dll"
mkdir -p "$DATA_DIR/plugins/EmbyStreams/libs"
cp "$SCRIPT_DIR/bin/Release/net8.0/publish/Polly.dll" "$DATA_DIR/plugins/EmbyStreams/libs/" 2>/dev/null || true
cp "$SCRIPT_DIR/bin/Release/net8.0/publish/Polly.Core.dll" "$DATA_DIR/plugins/EmbyStreams/libs/" 2>/dev/null || true
echo "      DLL deployed to $DATA_DIR/plugins/"
cp "$SCRIPT_DIR/plugin.json" "$DATA_DIR/plugins/"
echo "      plugin.json deployed to $DATA_DIR/plugins/"

# ── 5. Start Emby on port 8096 ────────────────────────────────────────────────
echo "[5/5] Starting Emby on port 8096..."
cd "$(dirname "$(readlink -f "$0")")/../emby-beta/opt/emby-server"
# Must set EMBY_DATA to override wrapper script's default of /var/lib/emby
export EMBY_DATA="$DATA_DIR"
nohup "$EMBY_BIN" \
    -port 8096 \
    -updatepackage none \
    > "$HOME/emby-dev.log" 2>&1 &

EMBY_PID=$!
echo "      PID $EMBY_PID — waiting 30s for startup..."
sleep 30

if ss -tlnp 2>/dev/null | grep -q 8096; then
    echo ""
    echo "✓ Emby is UP on port 8096"
    echo "  Web UI:    http://localhost:8096"
    echo "  Plugin:    http://localhost:8096/web/configurationpage?name=EmbyStreams"
    echo "  Logs:      $LOG_FILE"
else
    echo ""
    echo "✗ Port 8096 not listening — last 30 log lines:"
    tail -30 "$HOME/emby-dev.log" 2>/dev/null || tail -30 "$LOG_FILE" 2>/dev/null || echo "(no log yet)"
    exit 1
fi
