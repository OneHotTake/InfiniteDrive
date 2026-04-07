# Sprint 127 — Versioned Playback: Startup Detection (Server Address)

**Version:** v3.3 | **Status:** Plan | **Risk:** LOW | **Depends:** Sprint 124

---

## Overview

 Sprint 127 adds server address change detection. On every plugin startup, it compares the stored LAN address with the current address and triggers a URL rewrite sweep if they differ.

 **Key Principle:** Server address changes are detected on startup (not on a timer). URL rewrite is a lightweight operation — only re `.strm` file content is rewritten, no candidate re-fetch required.

---

## Phase 127A — VersionPlaybackStartupDetector

### FIX-127A-01: VersionPlaybackStartupDetector Service

**File:** `Services/VersionPlaybackStartupDetector.cs` (create)

**What:** `IServerEntryPoint` that detects server address changes on startup and triggers URL rewrite sweep.

```csharp
public class VersionPlaybackStartupDetector : IServerEntryPoint, IDisposable
{
    public async Task Run()
    {
        // 1. Read stored LAN address from PluginConfiguration.LastKnownServerAddress
        // 2. Read current LAN address from IServerApplicationHost
        // 3. Compare (normalize both to "host:port" format)
        // 4. If different:
        //    a. Queue URL rewrite sweep for all materialized .strm files
        //    b. Update PluginConfiguration.LastKnownServerAddress
        //    c. Log the change
    }

    public void Dispose() { }
}
```

**URL Rewrite Sweep Logic:**
- Query `materialized_versions` for all `.strm` paths
- For each file: read current content, replace old base URL with new base URL
- Preserve all query parameters (`titleId`, `slot`, `token`)
- Rate limit: use existing `ApiCallDelayMs` between file rewrites
- Trigger single Emby library scan on completion
- No candidate re-fetch required — URL rewrite only

**Depends on:** Sprint 122 (materialized_versions table), Sprint 123 (materialized versions)
**Must not break:** Plugin startup sequence. This runs after database initialization.

---

### FIX-127A-02: Add LastKnownServerAddress to PluginConfiguration

**File:** `PluginConfiguration.cs` (modify)

**What:** Add config field to store the last known server address.

```csharp
[DataMember]
public string LastKnownServerAddress { get; set; } = string.Empty;
```

**Why:** Needed to detect address changes between startups.

**Depends on:** None
**Must not break:** Existing config fields.

---

## Sprint 127 Dependencies

- **Previous Sprint:** 124 (Playback Endpoint)
- **Blocked By:** Sprint 124
- **Blocks:** Sprint 128 (Plugin Registration)

---

## Sprint 127 Completion Criteria

- [ ] Startup detector runs on every plugin startup
- [ ] Detects server address change (host:port comparison)
- [ ] Triggers URL rewrite sweep when address changes
- [ ] URL rewrite preserves all query parameters
- [ ] URL rewrite rate-limited through existing trickle pipeline
- [ ] Single Emby library scan triggered on completion
- [ ] PluginConfiguration.LastKnownServerAddress updated after detection
- [ ] Build succeeds ( 0 warnings, 0 errors)

---

## Sprint 127 Notes

 **Address Comparison:**
- Normalize both addresses to `http(s)://host:port` format
- Ignore trailing slashes
- Compare case-insensitive
- If current address is null/empty (first startup), store it without triggering sweep

 **URL Rewrite Pattern:**
- Old: `http://192.168.1.100:8096/EmbyStreams/play?titleId=tt123&slot=hd_broad&token=abc`
- New: `http://192.168.1.200:8096/EmbyStreams/play?titleId=tt123&slot=hd_broad&token=abc`
- Only the base URL (protocol + host + port) changes
- All query parameters preserved exactly

 **Performance:**
- For large catalogs (1000+ items), rewrite sweep may take several minutes
- Rate limiting prevents disk I/O saturation
- Library scan triggered only once at end

 **First Startup Behavior:**
- If `LastKnownServerAddress` is empty (fresh install), store current address
- Do NOT trigger URL rewrite sweep on fresh install (no files exist yet)
