#!/bin/bash
# =============================================================================
# InfiniteDrive Development Server Startup Script
# =============================================================================
# Purpose: Run an isolated Emby development server on port 8096 for testing
#          the InfiniteDrive plugin, completely separate from production.
#
# What this script does:
#   1. Builds the InfiniteDrive plugin (Release mode)
#   2. Copies the DLL to ~/emby-dev-data/plugins/
#   3. Kills any existing emby processes
#   4. Starts Emby Server on port 8096 using isolated data directory
#
# Key directories:
#   - ../emby-beta/         -> Emby beta installation
#   - ~/emby-dev-data/      -> Isolated data directory
#
# See RUNBOOK.md for full documentation and troubleshooting.
# =============================================================================

set -e

export PATH="$PATH:$HOME/.dotnet"

cd /home/onehottake/Projects/emby/embyStreams

echo "=== Building InfiniteDrive plugin ==="
dotnet publish -c Release || { echo "Build failed"; exit 1; }

echo "=== Deploying plugin to dev directory ==="
mkdir -p ~/emby-dev-data/plugins/InfiniteDrive/libs
cp bin/Release/net8.0/publish/InfiniteDrive.dll ~/emby-dev-data/plugins/
cp bin/Release/net8.0/publish/Polly.dll ~/emby-dev-data/plugins/InfiniteDrive/libs/ 2>/dev/null || true
cp bin/Release/net8.0/publish/Polly.Core.dll ~/emby-dev-data/plugins/InfiniteDrive/libs/ 2>/dev/null || true
cp plugin.json ~/emby-dev-data/plugins/

echo "=== Checking for existing emby processes ==="
pkill -f "emby-server" 2>/dev/null || true
sleep 2

echo "=== Starting Emby Server on port 8096 ==="
export EMBY_DATA="$HOME/emby-dev-data"
export XDG_CACHE_HOME="$EMBY_DATA/cache"

cd "$(dirname "$(readlink -f "$0")")/../emby-beta/opt/emby-server"

nohup ./bin/emby-server \
  -port 8096 \
  -updatepackage none \
  > "$HOME/emby-dev.log" 2>&1 &

EMBY_PID=$!
echo "EmbyServer started with PID: $EMBY_PID"

echo "=== Waiting for server to start (30s) ==="
sleep 30

echo "=== Checking if port 8096 is listening ==="
if ss -tlnp | grep -q 8096; then
    echo "SUCCESS: Server is listening on port 8096"
else
    echo "WARNING: Port 8096 not found, checking logs..."
    tail -50 ~/emby-dev.log
fi

echo ""
echo "=== Dev environment ready ==="
echo "Server: http://localhost:8096"
echo "Plugin config: http://localhost:8096/web/configurationpage?name=InfiniteDrive"
echo "Logs: ~/emby-dev.log or ~/emby-dev-data/logs/embyserver.txt"
echo ""
echo "For troubleshooting, see RUNBOOK.md"
