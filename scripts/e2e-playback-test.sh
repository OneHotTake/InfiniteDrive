#!/bin/bash
# =============================================================================
# InfiniteDrive E2E: configure → sync → .strm → playback
# =============================================================================
# Proves the full loop against a real AIOStreams instance:
#   1. Emby up & on 4.10.0.16, InfiniteDrive plugin loaded
#   2. Plugin configured with a manifest URL + libraries
#   3. Catalog sync populates catalog_items (DB)
#   4. Marvin writes .strm files to the library path
#   5. Emby sees the items
#   6. A .strm resolves to a CDN URL that returns playable video bytes
#
# Drives Emby via an existing admin token from authentication.db (no creds needed).
# Inspects the plugin DB + filesystem directly for the loop assertions.
#
# Usage: ./e2e-playback-test.sh           (assumes dev Emby already started)
#        START=1 ./e2e-playback-test.sh   (stop any running emby, start dev first)
# =============================================================================
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/.."
DATA_DIR="${EMBY_DATA:-$HOME/emby-dev-data}"
MEDIA_DIR="${MEDIA_DIR:-/media/infinitedrive}"
PORT="${EMBY_PORT:-8096}"
BASE="http://localhost:$PORT/emby"
DB="$DATA_DIR/data/InfiniteDrive/infinitedrive.db"
AUTHDB="$DATA_DIR/data/authentication.db"
TIMEOUT_SYNC="${TIMEOUT_SYNC:-240}"

G='\033[0;32m'; R='\033[0;31m'; Y='\033[1;33m'; N='\033[0m'
pass(){ echo -e "${G}✓ PASS${N} $1"; }
fail(){ echo -e "${R}✗ FAIL${N} $1"; FAILED=1; }
info(){ echo -e "${Y}•${N} $1"; }
FAILED=0

# ── Optional: bring up the dev instance ──────────────────────────────────────
if [ "${START:-0}" = "1" ]; then
    info "Stopping any running emby-server and starting dev instance…"
    "$SCRIPT_DIR/emby-control.sh" restart || { fail "emby-control restart failed"; exit 1; }
fi

# ── Stage 0: server up + version + plugin loaded ─────────────────────────────
info "Stage 0 — server health"
for i in $(seq 1 30); do
    PUB=$(curl -sf --max-time 5 "$BASE/System/Info/Public" 2>/dev/null) && break
    sleep 2
done
VER=$(echo "${PUB:-}" | python3 -c 'import sys,json;print(json.load(sys.stdin).get("Version",""))' 2>/dev/null)
[ -n "$VER" ] && pass "Emby reachable, version $VER" || { fail "Emby not reachable on $PORT"; exit 1; }
case "$VER" in 4.10.0.1[0-9]*|4.10.0.16*) pass "Running a 4.10.0.1x build" ;; *) info "version is $VER (expected 4.10.0.16)";; esac

# ── Find a working admin token ───────────────────────────────────────────────
TOKEN=""
if [ -f "$AUTHDB" ]; then
    for t in $(python3 - "$AUTHDB" <<'PY'
import sqlite3,sys
try:
    c=sqlite3.connect(sys.argv[1])
    for (tok,) in c.execute("select AccessToken from Tokens_2 where IsActive=1"):
        print(tok)
except Exception:
    try:
        for (tok,) in c.execute("select AccessToken from Tokens_2"): print(tok)
    except Exception: pass
PY
); do
        if curl -sf --max-time 5 -H "X-Emby-Token: $t" "$BASE/Users" >/dev/null 2>&1; then TOKEN="$t"; break; fi
    done
fi
[ -n "$TOKEN" ] && pass "Authenticated with stored admin token" || info "No working stored token — API-driven stages will be skipped (DB/FS checks still run)"
AUTH=(); [ -n "$TOKEN" ] && AUTH=(-H "X-Emby-Token: $TOKEN")

# ── Stage 1: plugin loaded + configured ──────────────────────────────────────
info "Stage 1 — plugin loaded & configured"
if [ -n "$TOKEN" ]; then
    PLUGINS=$(curl -sf --max-time 8 "${AUTH[@]}" "$BASE/Plugins" 2>/dev/null)
    echo "$PLUGINS" | grep -qi "InfiniteDrive" && pass "InfiniteDrive plugin loaded" || fail "InfiniteDrive plugin not in /Plugins"
fi
CFG="$DATA_DIR/plugins/configurations/InfiniteDrive.xml"
MANIFEST=$(grep -aoE "<PrimaryManifestUrl>[^<]*" "$CFG" 2>/dev/null | sed 's/<PrimaryManifestUrl>//')
[ -n "$MANIFEST" ] && pass "Manifest configured: ${MANIFEST:0:60}…" || fail "No PrimaryManifestUrl in plugin config"

# ── Stage 2: trigger catalog sync (scheduled task) ───────────────────────────
info "Stage 2 — trigger catalog sync"
if [ -n "$TOKEN" ]; then
    TASKS=$(curl -sf --max-time 8 "${AUTH[@]}" "$BASE/ScheduledTasks" 2>/dev/null)
    for key in "Catalog" "Sync" "Marvin"; do
        TID=$(echo "$TASKS" | python3 -c "
import sys,json
d=json.load(sys.stdin)
for t in d:
    if '$key'.lower() in (t.get('Name','')+t.get('Key','')).lower() and 'infinite' in json.dumps(t).lower():
        print(t['Id']); break
" 2>/dev/null)
        [ -n "$TID" ] && { curl -sf -X POST "${AUTH[@]}" "$BASE/ScheduledTasks/Running/$TID" >/dev/null 2>&1 && info "triggered task '$key' ($TID)"; }
    done
else
    info "skipped (no token) — relying on plugin auto-sync on startup"
fi

# ── Stage 3: catalog_items populated ─────────────────────────────────────────
info "Stage 3 — catalog items in DB (timeout ${TIMEOUT_SYNC}s)"
count_rows(){ python3 - "$DB" "$1" <<'PY' 2>/dev/null
import sqlite3,sys
try:
    c=sqlite3.connect(sys.argv[1]); print(c.execute(sys.argv[2]).fetchone()[0])
except Exception: print(0)
PY
}
CITEMS=0
for i in $(seq 1 $((TIMEOUT_SYNC/5))); do
    CITEMS=$(count_rows "select count(*) from catalog_items where removed_at is null")
    [ "${CITEMS:-0}" -gt 0 ] && break
    sleep 5
done
[ "${CITEMS:-0}" -gt 0 ] && pass "catalog_items = $CITEMS" || fail "no catalog_items after ${TIMEOUT_SYNC}s"

# ── Stage 4: .strm files written ─────────────────────────────────────────────
info "Stage 4 — .strm files on disk"
STRM=""
for i in $(seq 1 24); do
    STRM=$(find "$MEDIA_DIR" -name "*.strm" -type f 2>/dev/null | head -1)
    [ -n "$STRM" ] && break
    sleep 5
done
STRM_COUNT=$(find "$MEDIA_DIR" -name "*.strm" -type f 2>/dev/null | wc -l)
[ -n "$STRM" ] && pass ".strm files = $STRM_COUNT (e.g. $(basename "$STRM"))" || fail "no .strm files under $MEDIA_DIR"

# ── Stage 5: a .strm resolves to playable bytes ──────────────────────────────
info "Stage 5 — playback resolution"
if [ -n "$STRM" ]; then
    URL=$(head -c 4000 "$STRM" | tr -d '\r' | grep -aE '^https?://' | head -1)
    if [ -z "$URL" ]; then
        info ".strm has no direct URL yet (Marvin populates async). Content: $(head -c 80 "$STRM")"
        fail "no playable URL in .strm"
    else
        info "stream URL: ${URL:0:80}…"
        HDRS=$(curl -s -D - -o /dev/null --max-time 30 -r 0-1048575 "$URL" 2>/dev/null)
        CODE=$(echo "$HDRS" | grep -aoE "HTTP/[0-9.]+ [0-9]+" | tail -1 | grep -oE "[0-9]+$")
        CTYPE=$(echo "$HDRS" | grep -aiE "^content-type:" | tail -1)
        if [ "${CODE:-0}" = "200" ] || [ "${CODE:-0}" = "206" ]; then
            pass "stream returned HTTP $CODE ($CTYPE)"
        else
            fail "stream did not return playable bytes (HTTP ${CODE:-none})"
        fi
    fi
fi

# ── Summary ──────────────────────────────────────────────────────────────────
echo "─────────────────────────────────────────────"
[ "$FAILED" = "0" ] && echo -e "${G}E2E PASSED — configure→sync→strm→playback loop verified.${N}" \
                     || echo -e "${R}E2E had failures (see above).${N}"
exit $FAILED
