#!/bin/bash
# =============================================================================
# InfiniteDrive Emby Control Script
# =============================================================================
# Usage: ./emby-control.sh {start|stop|restart|reset}
#
# Commands:
#   start   - Build plugin (if needed), deploy, and start Emby on port 8096
#   stop    - Gracefully stop Emby (with force-kill fallback)
#   restart - Stop, then start
#   reset   - Full wipe: stop, cleanup all state, rebuild plugin, start
#
# Environment:
#   - EMBY_DATA:   ~/emby-dev-data (isolated data directory)
#   - EMBY_PORT:   8096
#   - MEDIA_DIR:   /media/infinitedrive
# =============================================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/.."
EMBY_BIN="${EMBY_BIN:-/opt/emby-server/bin/emby-server}"
DATA_DIR="${EMBY_DATA:-$HOME/emby-dev-data}"
LOG_FILE="$DATA_DIR/logs/embyserver.txt"
MEDIA_DIR="${MEDIA_DIR:-/media/infinitedrive}"
EMBY_PORT="${EMBY_PORT:-8096}"

export LD_LIBRARY_PATH=/usr/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}
export PATH="$PATH:$HOME/.dotnet"

# Color codes for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

log_info()    { echo -e "${GREEN}[INFO]${NC} $1"; }
log_warn()    { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error()   { echo -e "${RED}[ERROR]${NC} $1"; }

# =============================================================================
# Subcommand: stop
# =============================================================================
stop_emby() {
    log_info "Stopping Emby (PID $(pgrep -f emby-server 2>/dev/null | head -1 || echo 'not found'))..."

    # Try graceful shutdown first
    pkill -TERM -f "emby-server" 2>/dev/null || true

    # Wait up to 10 seconds
    local count=0
    while [ $count -lt 10 ]; do
        if ! pgrep -f "emby-server" > /dev/null 2>&1; then
            log_info "Emby stopped gracefully"
            return 0
        fi
        sleep 1
        ((count++))
    done

    # Force kill if still running
    if pgrep -f "emby-server" > /dev/null 2>&1; then
        log_warn "Emby did not stop gracefully, force-killing..."
        pkill -9 -f "emby-server" 2>/dev/null || true
        sleep 1
    fi

    if pgrep -f "emby-server" > /dev/null 2>&1; then
        log_error "Failed to stop Emby"
        return 1
    fi

    log_info "Emby stopped"
}

# =============================================================================
# Subcommand: cleanup (used by reset)
# =============================================================================
cleanup_state() {
    log_info "Cleaning up state..."

    # 1. Delete .strm and .nfo files from media directory
    log_info "  Deleting .strm/.nfo files from $MEDIA_DIR..."
    find "$MEDIA_DIR" -name "*.strm" -type f -delete 2>/dev/null || true
    find "$MEDIA_DIR" -name "*.nfo" -type f -delete 2>/dev/null || true
    find "$MEDIA_DIR" -mindepth 2 -type d -empty -delete 2>/dev/null || true

    # 2. Delete plugin databases and configuration
    log_info "  Deleting plugin databases..."
    rm -rf "$DATA_DIR/data/InfiniteDrive/"* 2>/dev/null || true

    log_info "  Deleting plugin configuration..."
    rm -f "$DATA_DIR/plugins/configurations/InfiniteDrive.xml" 2>/dev/null || true

    # 3. Clear all Emby state (except plugins)
    log_info "  Clearing Emby data directories..."
    for dir in cache config data logs metadata transcoding-temp; do
        rm -rf "$DATA_DIR/$dir" 2>/dev/null || true
        mkdir -p "$DATA_DIR/$dir"
    done

    # Ensure plugins directory exists
    mkdir -p "$DATA_DIR/plugins"

    log_info "Cleanup complete"
}

# =============================================================================
# Subcommand: start
# =============================================================================
start_emby() {
    local force_build="${1:-false}"

    # Build if forced or DLL doesn't exist
    local dll="$PROJECT_DIR/bin/Release/net8.0/publish/InfiniteDrive.dll"
    if [ "$force_build" = "true" ] || [ ! -f "$dll" ]; then
        log_info "Building InfiniteDrive plugin..."
        cd "$PROJECT_DIR"
        dotnet publish -c Release || { log_error "Build failed"; exit 1; }
    fi

    # Deploy plugin
    log_info "Deploying plugin to $DATA_DIR/plugins/..."
    mkdir -p "$DATA_DIR/plugins/InfiniteDrive/libs"

    cp "$PROJECT_DIR/bin/Release/net8.0/publish/InfiniteDrive.dll" "$DATA_DIR/plugins/" || { log_error "DLL not found"; exit 1; }
    cp "$PROJECT_DIR/bin/Release/net8.0/publish/Polly.dll" "$DATA_DIR/plugins/InfiniteDrive/libs/" 2>/dev/null || true
    cp "$PROJECT_DIR/bin/Release/net8.0/publish/Polly.Core.dll" "$DATA_DIR/plugins/InfiniteDrive/libs/" 2>/dev/null || true
    cp "$PROJECT_DIR/plugin.json" "$DATA_DIR/plugins/"

    # Deploy UI files to both plugin dir and Emby's config cache
    for dir in "$DATA_DIR/plugins/InfiniteDrive" "$DATA_DIR/plugins/configurations/InfiniteDrive"; do
        mkdir -p "$dir" 2>/dev/null || true
        cp "$PROJECT_DIR/Configuration/configurationpage.html" "$dir/" 2>/dev/null || true
        cp "$PROJECT_DIR/Configuration/configurationpage.js" "$dir/" 2>/dev/null || true
        cp "$PROJECT_DIR/bin/Release/net8.0/publish/InfiniteDrive.dll" "$dir/" 2>/dev/null || true
    done

    # Ensure log directory exists
    mkdir -p "$DATA_DIR/logs"

    # Start Emby
    log_info "Starting Emby on port $EMBY_PORT..."

    cd "$(dirname "$EMBY_BIN")"
    export EMBY_DATA="$DATA_DIR"

    nohup "$EMBY_BIN" \
        -port "$EMBY_PORT" \
        -updatepackage none \
        > "$HOME/emby-dev.log" 2>&1 &

    local emby_pid=$!
    log_info "Emby started with PID $emby_pid, waiting for startup..."

    # Wait for port to be listening
    local count=0
    while [ $count -lt 30 ]; do
        if ss -tlnp 2>/dev/null | grep -q ":$EMBY_PORT "; then
            log_info "Emby is UP on port $EMBY_PORT"
            echo ""
            echo "  Web UI:    http://localhost:$EMBY_PORT"
            echo "  Plugin:    http://localhost:$EMBY_PORT/web/configurationpage?name=InfiniteDrive"
            echo "  Logs:      $LOG_FILE"
            echo "  Dev log:   $HOME/emby-dev.log"
            return 0
        fi
        sleep 1
        ((count++))
    done

    log_error "Port $EMBY_PORT not listening after 30s"
    echo ""
    echo "Last 30 log lines:"
    tail -30 "$HOME/emby-dev.log" 2>/dev/null || tail -30 "$LOG_FILE" 2>/dev/null || echo "(no log file)"
    return 1
}

# =============================================================================
# Subcommand: restart
# =============================================================================
restart_emby() {
    stop_emby
    sleep 1
    start_emby false  # Don't force rebuild
}

# =============================================================================
# Subcommand: reset
# =============================================================================
reset_emby() {
    stop_emby
    sleep 1
    cleanup_state
    start_emby true  # Force rebuild
}

# =============================================================================
# Main
# =============================================================================
case "${1:-}" in
    start)
        start_emby false
        ;;
    stop)
        stop_emby
        ;;
    restart)
        restart_emby
        ;;
    reset)
        reset_emby
        ;;
    *)
        echo "Usage: $0 {start|stop|restart|reset}" >&2
        exit 1
        ;;
esac
