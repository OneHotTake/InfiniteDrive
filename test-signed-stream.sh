#!/bin/bash
# Test script for /EmbyStreams/Stream endpoint (v0.56.2)

set -e

EMBY_BASE="http://localhost:8096"
IMDB_ID="tt0133093"
MEDIA_TYPE="movie"
PLUGIN_SECRET="dGVzdFNlY3JldDEyMzQ1Njc4OTAx"  # Test secret (base64)

echo "=== Sprint 56.2: Testing /EmbyStreams/Stream Endpoint ==="
echo ""

# Helper function to compute HMAC-SHA256 (message: id:type:season:episode:exp)
compute_hmac() {
    local id=$1
    local type=$2
    local season=$3
    local episode=$4
    local exp=$5
    local secret=$6

    # Build message: id:type:season:episode:exp
    # season/episode are empty strings if null
    local message="${id}:${type}:${season}:${episode}:${exp}"

    # Decode secret from base64
    local secret_bytes=$(echo -n "$secret" | base64 -d)

    # HMAC-SHA256
    echo -n "$message" | openssl dgst -sha256 -mac HMAC -macopt "key=$(echo -n "$secret_bytes" | od -An -tx1 | tr -d ' ')" 2>/dev/null || \
    echo -n "$message" | openssl dgst -sha256 -hmac "$secret" | awk '{print $2}'
}

# Test 1: Valid signature with current timestamp
echo "--- Test 1: Valid Signature (movie) ---"
EXP=$(($(date +%s) + 3600))  # 1 hour from now
SIG=$(compute_hmac "$IMDB_ID" "$MEDIA_TYPE" "" "" "$EXP" "$PLUGIN_SECRET")

echo "ID: $IMDB_ID"
echo "Type: $MEDIA_TYPE"
echo "Expiry: $EXP"
echo "Signature: $SIG"

URL="${EMBY_BASE}/EmbyStreams/Stream?id=${IMDB_ID}&type=${MEDIA_TYPE}&exp=${EXP}&sig=${SIG}"
echo "URL: $URL"
echo ""
echo "curl test (should return 302 or stream response):"
curl -v "$URL" 2>&1 | head -30
echo ""
echo ""

# Test 2: Expired signature
echo "--- Test 2: Expired Signature ---"
EXP=$(($(date +%s) - 3600))  # 1 hour ago
SIG=$(compute_hmac "$IMDB_ID" "$MEDIA_TYPE" "" "" "$EXP" "$PLUGIN_SECRET")

echo "ID: $IMDB_ID"
echo "Type: $MEDIA_TYPE"
echo "Expiry: $EXP (in the past)"
echo "Signature: $SIG"

URL="${EMBY_BASE}/EmbyStreams/Stream?id=${IMDB_ID}&type=${MEDIA_TYPE}&exp=${EXP}&sig=${SIG}"
echo ""
echo "curl test (should return 403):"
curl -v "$URL" 2>&1 | head -20
echo ""
echo ""

# Test 3: Invalid signature
echo "--- Test 3: Invalid Signature ---"
EXP=$(($(date +%s) + 3600))
BAD_SIG="0000000000000000000000000000000000000000000000000000000000000000"

echo "ID: $IMDB_ID"
echo "Type: $MEDIA_TYPE"
echo "Expiry: $EXP"
echo "Signature: $BAD_SIG (corrupted)"

URL="${EMBY_BASE}/EmbyStreams/Stream?id=${IMDB_ID}&type=${MEDIA_TYPE}&exp=${EXP}&sig=${BAD_SIG}"
echo ""
echo "curl test (should return 403):"
curl -v "$URL" 2>&1 | head -20
echo ""
echo ""

# Test 4: Series with season/episode
echo "--- Test 4: Valid Signature (series with season/episode) ---"
EXP=$(($(date +%s) + 3600))
SERIES_ID="tt0141894"
SIG=$(compute_hmac "$SERIES_ID" "series" "1" "5" "$EXP" "$PLUGIN_SECRET")

echo "ID: $SERIES_ID"
echo "Type: series"
echo "Season: 1"
echo "Episode: 5"
echo "Expiry: $EXP"
echo "Signature: $SIG"

URL="${EMBY_BASE}/EmbyStreams/Stream?id=${SERIES_ID}&type=series&season=1&episode=5&exp=${EXP}&sig=${SIG}"
echo ""
echo "curl test (should return 302 or stream response):"
curl -v "$URL" 2>&1 | head -30
echo ""

echo "=== Test Summary ==="
echo "✓ If Test 1 and 4 return 302/200, endpoint is working"
echo "✓ If Test 2 and 3 return 403, validation is working"
