# EmbyStreams End-to-End Test Plan

## Overview
Comprehensive user-facing functionality test suite for EmbyStreams plugin.
Tests all major user workflows from setup to playback.

## Test Environment
- **Server:** http://localhost:8096
- **Browser:** Chromium (via Playwright)
- **Test Data:** Known good IMDB IDs (movies and series)
- **Test Duration:** ~30 minutes

---

## Test Categories

### 1. Plugin Setup & Configuration

| Test ID | Description | Steps | Expected Result |
|----------|-------------|--------|-----------------|
| SETUP-01 | Access configuration page | Navigate to http://localhost:8096/web/configurationpage?name=EmbyStreams | Configuration page loads with all tabs visible |
| SETUP-02 | Plugin is loaded | Check Server logs for EmbyStreams entry points | No errors, all services started |
| SETUP-03 | View System Status | Click "Advanced" tab → "System Status" section | Shows version, AIOStreams connection, DB stats |
| SETUP-04 | Check database status | View catalog items count in System Status | Displays count (may be 0 on first run) |

### 2. AIOStreams Connection Test

| Test ID | Description | Steps | Expected Result |
|----------|-------------|--------|-----------------|
| CONN-01 | Test manifest URL health | Use test endpoint or curl to check AIOStreams reachability | Status shows "connected" or latency < 5000ms |
| CONN-02 | Invalid manifest URL | Enter invalid URL in configuration | Shows error message, connection status = "error" |
| CONN-03 | Valid manifest URL with auth | Enter valid AIOStreams URL with UUID/token | Shows status "ok" or "stale" |

### 3. Library Setup

| Test ID | Description | Steps | Expected Result |
|----------|-------------|--------|-----------------|
| LIB-01 | Create Movies library | In Setup tab, select "Movies" → enter library name | Saves configuration, creates directory |
| LIB-02 | Create Series library | In Setup tab, select "Series" → enter library name | Saves configuration |
| LIB-03 | Skip Anime library | In Setup tab, uncheck "Enable Anime Library" | Saves configuration |
| LIB-04 | Verify directories exist | Check filesystem for library paths | Directories created in sync path |

### 4. Catalog Sync

| Test ID | Description | Steps | Expected Result |
|----------|-------------|--------|-----------------|
| SYNC-01 | Manual sync trigger | In Advanced tab → "Catalog Sync" → Click "Sync Now" | Task starts, shows progress |
| SYNC-02 | View sync progress | Wait for sync to complete | Status shows "Last sync" timestamp |
| SYNC-03 | Check catalog items in DB | Query Status endpoint for catalog count | Count > 0 if sync succeeded |
| SYNC-04 | Verify .strm files created | Browse library filesystem | .strm files present for synced items |

### 5. Discover Feature

| Test ID | Description | Steps | Expected Result |
|----------|-------------|--------|-----------------|
| DISC-01 | Browse discover catalog | Navigate to Discover tab | Shows grid/list of available content |
| DISC-02 | Search discover catalog | Enter search term in search box | Shows filtered results |
| DISC-03 | View item details | Click on any item in catalog | Shows poster, title, plot, genres |
| DISC-04 | Add movie to library | Click "Add to Library" on a movie | Success message, item added to catalog |
| DISC-05 | Add series to library | Click "Add to Library" on a series | Success message, series added |
| DISC-06 | Verify library status | Check item shows "In Library" badge | Badge displays correctly |

### 6. Metadata & NFO Files

| Test ID | Description | Steps | Expected Result |
|----------|-------------|--------|-----------------|
| META-01 | Check movie .nfo | Browse to movie item directory | .nfo file present with IMDB ID, plot, etc. |
| META-02 | Check series .nfo | Browse to series directory | Series .nfo and episode .nfo files present |
| META-03 | Verify unique IDs | Open .nfo file | Contains IMDB, TMDB, and other provider IDs |
| META-04 | Metadata fallback | Force MetadataFallbackTask run | Missing metadata gets populated |

### 7. Stream Resolution & Playback

| Test ID | Description | Steps | Expected Result |
|----------|-------------|--------|-----------------|
| PLAY-01 | Play movie from Emby | In Emby web UI, select movie and play | Stream starts, video plays |
| PLAY-02 | Play series episode | In Emby web UI, select series episode and play | Stream starts, video plays |
| PLAY-03 | Check cache hit | Play same episode twice | Second play is faster (cache hit) |
| PLAY-04 | Check resolution stats | View Status → "Resolution Cache" section | Shows cache coverage % |
| PLAY-05 | Test signed stream URL | Use /EmbyStreams/Stream endpoint with valid signature | Returns HTTP 302 redirect to stream URL |

### 8. Stream Proxy & Range Requests

| Test ID | Description | Steps | Expected Result |
|----------|-------------|--------|-----------------|
| PROX-01 | Full stream playback | Play complete video | Entire content downloads successfully |
| PROX-02 | Range request (seek) | Seek to middle of video | Video resumes from seek position |
| PROX-03 | HEAD request | Send HEAD to stream URL | Returns Content-Length headers |
| PROX-04 | Client compatibility | Test with known redirect-capable client | Redirect works (not proxied) |

### 9. Doctor & Reconciliation

| Test ID | Description | Steps | Expected Result |
|----------|-------------|--------|-----------------|
| DOC-01 | Run doctor task | Trigger /EmbyStreams/Trigger?task=doctor | Doctor runs through all phases |
| DOC-02 | Check item states | View Status → "Catalog Stats" | Shows counts per ItemState |
| DOC-03 | Pin an item | Use Discover → "Add to Library" (pin option) | Item shows as PINNED |
| DOC-04 | Unpin an item | Remove from library (unpin) | Item state reverts to RETIRED or CATALOGUED |
| DOC-05 | Orphan cleanup | Delete real file, run doctor | .strm removed, catalog item cleaned up |

### 10. Rate Limiting

| Test ID | Description | Steps | Expected Result |
|----------|-------------|--------|-----------------|
| RATE-01 | Normal playback | Play 1 video | Success |
| RATE-02 | Rapid requests | Trigger 10+ playback requests in quick succession | After limit, returns 429 or error |
| RATE-03 | Rate limit recovery | Wait 1 minute, try again | Request succeeds again |

### 11. Resilience (Sprint 104C)

| Test ID | Description | Steps | Expected Result |
|----------|-------------|--------|-----------------|
| RESI-01 | Circuit breaker closed | Normal AIOStreams request | Succeeds |
| RESI-02 | Trigger circuit breaker | Fail AIOStreams 5+ times (simulate) | Circuit opens, requests fail fast |
| RESI-03 | Circuit breaker recovery | Wait 30 seconds | Circuit resets/closes |
| RESI-04 | Timeout | Request to slow/non-responsive endpoint | Returns timeout error after 15s |
| RESI-05 | Retry on transient failure | Simulate 500 error | Retries 3 times with backoff |

### 12. Webhook Integration

| Test ID | Description | Steps | Expected Result |
|----------|-------------|--------|-----------------|
| WEB-01 | POST webhook payload | Send {"imdb":"tt1234567"} to /EmbyStreams/Webhook/Sync | Creates .strm file, queues resolution |
| WEB-02 | Check webhook processing | Verify item in catalog | Item present in catalog_items table |

---

## Test Data (IMDB IDs)

### Movies
- `tt0133093` - The Matrix
- `tt0111161` - Jurassic Park
- `tt0088763` - Back to the Future

### Series
- `tt0944947` - Game of Thrones
- `tt0064649` - The X-Files
- `tt0903747` - Breaking Bad

### Anime (if enabled)
- `tt1564315` - One Punch Man
- `tt3032476` - My Hero Academia

---

## Test Execution Script

### Manual Browser Test

1. Open Chromium: `chromium --headless --remote-debugging-port=9222 http://localhost:8096`
2. Navigate to configuration page
3. Walk through Setup wizard (if first run)
4. Execute manual test cases above
5. Record results in test results table

### Automated Test (using curl)

```bash
#!/bin/bash
# EmbyStreams E2E Test Script

BASE_URL="http://localhost:8096"
RESULTS=()

# Test 1: Status endpoint
echo "Testing Status endpoint..."
STATUS=$(curl -s "$BASE_URL/EmbyStreams/Status")
if [[ $STATUS == *"emby-streams"* ]]; then
    echo "✓ PASS: Status endpoint works"
else
    echo "✗ FAIL: Status endpoint"
    RESULTS+=("Status endpoint")
fi

# Test 2: Plugin loaded
echo "Checking plugin logs..."
if tail -50 ~/emby-dev-data/logs/embyserver.txt | grep -q "EmbyStreams: \[Discover\] Initialization service ready"; then
    echo "✓ PASS: Plugin loaded"
else
    echo "✗ FAIL: Plugin not loaded"
    RESULTS+=("Plugin loaded")
fi

# Test 3: AIOStreams connection (requires configured manifest)
echo "Testing AIOStreams connection..."
# This would require valid config

# Test 4: Catalog items count
echo "Checking catalog count..."
# Parse from status endpoint

# Test 5: Stream resolution (requires valid IMDB)
echo "Testing stream resolution..."
STREAM=$(curl -s "$BASE_URL/EmbyStreams/Play?imdb=tt0133093&type=movie")
if [[ $STREAM == *"http"* ]] || [[ $STREAM == *"302"* ]]; then
    echo "✓ PASS: Stream resolution works"
else
    echo "✗ FAIL: Stream resolution"
    RESULTS+=("Stream resolution")
fi

echo ""
echo "=== Test Summary ==="
FAILED_COUNT=${#RESULTS[@]}
if [ $FAILED_COUNT -eq 0 ]; then
    echo "ALL TESTS PASSED ✓"
else
    echo "$FAILED_COUNT TEST(S) FAILED:"
    for result in "${RESULTS[@]}"; do
        echo "  - $result"
    done
fi
```

---

## Test Results Template

| Category | Test ID | Status | Notes | Timestamp |
|----------|----------|--------|-------|-----------|
| Setup | SETUP-01 | ⬜ PASS/FAIL | | |
| Setup | SETUP-02 | ⬜ PASS/FAIL | | |
| Connection | CONN-01 | ⬜ PASS/FAIL | | |
| Library | LIB-01 | ⬜ PASS/FAIL | | |
| Sync | SYNC-01 | ⬜ PASS/FAIL | | |
| Discover | DISC-01 | ⬜ PASS/FAIL | | |
| Metadata | META-01 | ⬜ PASS/FAIL | | |
| Playback | PLAY-01 | ⬜ PASS/FAIL | | |
| Doctor | DOC-01 | ⬜ PASS/FAIL | | |
| Rate Limiting | RATE-01 | ⬜ PASS/FAIL | | |
| Resilience | RESI-01 | ⬜ PASS/FAIL | | |
| Webhook | WEB-01 | ⬜ PASS/FAIL | | |

---

## Success Criteria

- **Critical Path Tests (MUST PASS):**
  - ✓ Plugin loads without errors
  - ✓ Configuration page accessible
  - ✓ Status endpoint returns valid JSON
  - ✓ Stream resolution returns URL or redirect
  - ✓ .strm files are created
  - ✓ NFO files contain metadata

- **Important Path Tests (SHOULD PASS):**
  - Catalog sync completes
  - Discover browse/search works
  - Playback starts successfully
  - Doctor runs without errors

- **Nice-to-Have Tests:**
  - All item states transition correctly
  - Rate limiting works
  - Resilience policies activate correctly

---

## Known Limitations

1. **Authentication Required:** Most endpoints require Emby authentication token
2. **Valid Config Needed:** AIOStreams functionality requires valid manifest URL
3. **Browser Dependencies:** Full UI testing requires Chromium/Playwright
4. **Test Data:** Some tests require items to be in catalog first

---

## Quick Smoke Test (5 minutes)

```bash
# 1. Start server
./emby-start.sh

# 2. Wait 30 seconds
sleep 30

# 3. Check plugin loaded
tail -30 ~/emby-dev-data/logs/embyserver.txt | grep "EmbyStreams"

# 4. Check status endpoint
curl -s http://localhost:8096/EmbyStreams/Status | jq '.'

# 5. Test playback (requires auth)
# curl -I "http://localhost:8096/EmbyStreams/Play?imdb=tt0133093&type=movie" \
#   -H "Authorization: Bearer YOUR_TOKEN"

echo "Smoke test complete!"
```
