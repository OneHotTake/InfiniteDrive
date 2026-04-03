#!/bin/bash
# =============================================================================
# Emby Dev Server Stop Script
# =============================================================================
# Purpose: Stop the Emby development server

set -e

echo "=== Stopping Emby development server ==="
pkill -f "emby-server" 2>/dev/null || true
sleep 2

echo "=== Checking if processes are still running ==="
if pgrep -f "emby-server" > /dev/null; then
    echo "WARNING: Some Emby processes still running, forcing kill"
    pkill -9 -f "emby-server" 2>/dev/null || true
fi

echo "=== Verifying all processes stopped ==="
if pgrep -f "emby-server" > /dev/null; then
    echo "ERROR: Failed to stop Emby server"
    exit 1
else
    echo "SUCCESS: Emby server stopped"
fi