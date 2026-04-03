# SignedStreamService Manual Test Plan

## Prerequisites
- Emby dev server running on http://localhost:9100
- Valid Emby user account with authentication token
- EmbyStreams plugin deployed

## Test Cases

### TC1: Valid HMAC Signature (Movie)
**Purpose**: Verify endpoint accepts valid movie stream request

**Steps**:
1. Get PluginSecret from plugin config
2. Generate signed URL for movie:
   - ID: `tt0133093` (The Matrix)
   - Type: `movie`
   - Expiry: Now + 1 hour
   - Signature: HMAC-SHA256 of `tt0133093:movie:::EXPIRY`
3. Call endpoint with Emby auth token:
   ```bash
   curl -X GET "http://localhost:9100/EmbyStreams/Stream?id=tt0133093&type=movie&exp=EXPIRY&sig=SIGNATURE" \
     -H "X-Emby-Token: YOUR_TOKEN"
   ```

**Expected Result**:
- HTTP 302 (or 502 if no stream available - that's OK, means signature validated)
- Location header points to real stream URL OR error JSON with `no_stream_available`

---

### TC2: Valid HMAC Signature (Series Episode)
**Purpose**: Verify endpoint accepts valid series episode request

**Steps**:
1. Generate signed URL for series:
   - ID: `tt0141894` (Breaking Bad S1E1)
   - Type: `series`
   - Season: `1`
   - Episode: `1`
   - Expiry: Now + 1 hour
   - Signature: HMAC-SHA256 of `tt0141894:series:1:1:EXPIRY`
2. Call endpoint with auth token

**Expected Result**: HTTP 302 or 502 (signature validated)

---

### TC3: Expired Signature
**Purpose**: Verify endpoint rejects expired signatures

**Steps**:
1. Generate signed URL with expiry = Now - 1 hour
2. Call endpoint

**Expected Result**: HTTP 403 with `{"error": "signature_expired"}`

---

### TC4: Invalid Signature (Tampered)
**Purpose**: Verify endpoint rejects tampered signatures

**Steps**:
1. Generate valid signature
2. Modify one character of the signature
3. Call endpoint

**Expected Result**: HTTP 403 with `{"error": "invalid_signature"}`

---

### TC5: Missing Parameters
**Purpose**: Verify endpoint rejects incomplete requests

**Steps**:
1. Call endpoint without `sig` parameter
2. Call endpoint without `exp` parameter
3. Call endpoint without `id` parameter

**Expected Result**: HTTP 400 with `{"error": "missing_parameters"}`

---

### TC6: No Valid Stream Available
**Purpose**: Verify graceful handling when stream can't be resolved

**Steps**:
1. Use valid HMAC signature
2. Use IMDB ID that has no available stream in catalog
3. Call endpoint

**Expected Result**: HTTP 502 with `{"error": "no_stream_available"}`

---

### TC7: 302 Redirect to Real Stream
**Purpose**: Verify successful redirect to debrid CDN URL

**Steps**:
1. Use IMDB ID that has cached stream available (e.g., from previous sync)
2. Call endpoint with valid signature
3. Follow redirect

**Expected Result**:
- HTTP 302 redirect
- Location header contains actual CDN URL (e.g., https://...")
- Can download/stream from Location URL

---

## Current Status

**NOT YET TESTED** - Dev environment doesn't have working Emby user authentication setup.

To complete testing:
1. Set up Emby admin user with known password
2. Get authentication token
3. Run TC1-TC7 above
4. Verify all pass

## Known Issues in Dev Environment

- Emby user "onehottake" exists but password unknown
- Account lockout after failed auth attempts (security feature)
- No first-run setup wizard accessible

## Expected Behavior Summary

| Test | Valid Sig | Valid Exp | Auth Token | Expected Result |
|------|-----------|-----------|------------|-----------------|
| TC1  | ✓ | ✓ | ✓ | 302 or 502 |
| TC2  | ✓ | ✓ | ✓ | 302 or 502 |
| TC3  | ✓ | ✗ | ✓ | 403 (expired) |
| TC4  | ✗ | ✓ | ✓ | 403 (invalid) |
| TC5  | N/A | N/A | ✓ | 400 (missing) |
| TC6  | ✓ | ✓ | ✓ | 502 (no stream) |
| TC7  | ✓ | ✓ | ✓ | 302 → CDN URL |
