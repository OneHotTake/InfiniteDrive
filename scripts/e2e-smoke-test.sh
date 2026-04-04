#!/bin/bash
# =============================================================================
# EmbyStreams E2E Smoke Test Script
# =============================================================================
# Quick verification of critical plugin functionality.
# Tests that don't require Emby authentication.

set -e

BASE_URL="http://localhost:8096"
LOG_FILE="$HOME/emby-dev-data/logs/embyserver.txt"
PASSED=0
FAILED=0
RESULTS=()

# Colors for output
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo "============================================"
echo "EmbyStreams E2E Smoke Test"
echo "============================================"
echo ""

# Test 1: Server is running
echo -n "[1/8] Server listening on port 8096... "
if ss -tlnp 2>/dev/null | grep -q 8096; then
    echo -e "${GREEN}✓ PASS${NC}"
    ((PASSED++))
else
    echo -e "${RED}✗ FAIL${NC}"
    echo "  Server not running!"
    RESULTS+=("Server listening")
    ((FAILED++))
    exit 1
fi

# Test 2: Plugin loaded (check logs)
echo -n "[2/8] Plugin loaded... "
if [ -f "$LOG_FILE" ] && tail -100 "$LOG_FILE" | grep -q "EmbyStreams: \[Discover\] Initialization service ready"; then
    echo -e "${GREEN}✓ PASS${NC}"
    ((PASSED++))
else
    echo -e "${RED}✗ FAIL${NC}"
    RESULTS+=("Plugin loaded")
    ((FAILED++))
fi

# Test 3: EmbyEventHandler loaded
echo -n "[3/8] EmbyEventHandler started... "
if [ -f "$LOG_FILE" ] && tail -100 "$LOG_FILE" | grep -q "EmbyStreams: \[EmbyStreams\] EmbyEventHandler started"; then
    echo -e "${GREEN}✓ PASS${NC}"
    ((PASSED++))
else
    echo -e "${RED}✗ FAIL${NC}"
    RESULTS+=("EmbyEventHandler")
    ((FAILED++))
fi

# Test 4: No Polly errors in logs
echo -n "[4/8] No Polly errors... "
if [ -f "$LOG_FILE" ] && ! tail -100 "$LOG_FILE" | grep -qi "polly.*error\|could not load.*polly"; then
    echo -e "${GREEN}✓ PASS${NC}"
    ((PASSED++))
else
    echo -e "${RED}✗ FAIL${NC}"
    RESULTS+=("Polly errors")
    ((FAILED++))
fi

# Test 5: Status endpoint accessible
echo -n "[5/8] Status endpoint accessible... "
STATUS_RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" "$BASE_URL/EmbyStreams/Status" 2>/dev/null || echo "000")
if [ "$STATUS_RESPONSE" = "200" ] || [ "$STATUS_RESPONSE" = "401" ]; then
    # 401 is expected (auth required), but endpoint is working
    echo -e "${GREEN}✓ PASS${NC} (HTTP $STATUS_RESPONSE)"
    ((PASSED++))
else
    echo -e "${RED}✗ FAIL${NC} (HTTP $STATUS_RESPONSE)"
    RESULTS+=("Status endpoint")
    ((FAILED++))
fi

# Test 6: Status endpoint returns valid JSON
echo -n "[6/8] Status JSON valid... "
if command -v jq &> /dev/null; then
    JSON_VALID=$(curl -s "$BASE_URL/EmbyStreams/Status" | jq '.' > /dev/null 2>&1 && echo "true" || echo "false")
    if [ "$JSON_VALID" = "true" ]; then
        echo -e "${GREEN}✓ PASS${NC}"
        ((PASSED++))
    else
        echo -e "${RED}✗ FAIL${NC}"
        RESULTS+=("Status JSON")
        ((FAILED++))
    fi
else
    echo -e "${YELLOW}⚠ SKIP (jq not installed)${NC}"
fi

# Test 7: Trigger endpoint accessible
echo -n "[7/8] Trigger endpoint accessible... "
TRIGGER_RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" "$BASE_URL/EmbyStreams/Trigger?task=doctor" 2>/dev/null || echo "000")
if [ "$TRIGGER_RESPONSE" = "200" ] || [ "$TRIGGER_RESPONSE" = "401" ]; then
    echo -e "${GREEN}✓ PASS${NC} (HTTP $TRIGGER_RESPONSE)"
    ((PASSED++))
else
    echo -e "${RED}✗ FAIL${NC} (HTTP $TRIGGER_RESPONSE)"
    RESULTS+=("Trigger endpoint")
    ((FAILED++))
fi

# Test 8: Check for critical errors in recent logs
echo -n "[8/8] No critical errors... "
if [ -f "$LOG_FILE" ] && ! tail -200 "$LOG_FILE" | grep -qi "emby.*exception\|emby.*fatal\|emby.*crash"; then
    echo -e "${GREEN}✓ PASS${NC}"
    ((PASSED++))
else
    echo -e "${RED}✗ FAIL${NC}"
    RESULTS+=("Critical errors")
    ((FAILED++))
fi

# Print Summary
echo ""
echo "============================================"
echo "Test Summary"
echo "============================================"
echo -e "${GREEN}Passed:${NC} $PASSED"
echo -e "${RED}Failed:${NC} $FAILED"
echo "Total:   $((PASSED + FAILED))"
echo ""

if [ $FAILED -gt 0 ]; then
    echo -e "${RED}Failed Tests:${NC}"
    for result in "${RESULTS[@]}"; do
        echo "  - $result"
    done
    echo ""
    echo "Check logs: tail -100 $LOG_FILE"
    exit 1
else
    echo -e "${GREEN}All tests passed! ✓${NC}"
    echo ""
    echo "Next steps:"
    echo "  1. Open browser to: $BASE_URL/web/configurationpage?name=EmbyStreams"
    echo "  2. Run setup wizard"
    echo "  3. Trigger catalog sync"
    echo "  4. Test playback in Emby UI"
    exit 0
fi
