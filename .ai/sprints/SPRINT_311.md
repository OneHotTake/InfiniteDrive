# Sprint 311 — Self-Healing Provider Failover & Sync Improvements

**Status:** Draft | **Risk:** LOW | **Depends:** Sprint 310 | **Target:** v0.42

## Why
When the primary provider fails, users shouldn't notice — the system should silently use the backup and restore when primary recovers. Additionally, 6-hour sync intervals are too conservative for the lightweight catalog fetch operation, and users get frustrated by ghost episodes and stale `no_streams` sentinels.

## Non-Goals
- Parallel provider attempts (unnecessary complexity)
- Provider scoring or latency-based selection
- Persisting active provider across restarts (self-corrects on first failure)
- Enterprise-scale concurrency handling

---

## Tasks

### FIX-311-01: Add ActiveProvider state
**Files:** `Plugin.cs` (modify), `Models/ActiveProviderState.cs` (create)
**Effort:** S
**What:** Add `ActiveProvider` enum (`Primary`, `Secondary`) and a singleton `ActiveProviderState` class in Plugin.cs with a single property: `public ActiveProvider Current { get; set; } = ActiveProvider.Primary;`. Thread-safe via simple lock or `volatile`. **Gotcha:** Keep it simple — one field, no history, no timestamps.

### FIX-311-02: Swap provider on failover success
**Files:** `Services/StreamResolutionHelper.cs` (modify)
**Effort:** S
**What:** In `SyncResolveViaProvidersAsync`, after successfully resolving via the non-active provider, set `Plugin.Instance.ActiveProviderState.Current` to that provider. Log at Warn: `"[Failover] Primary unavailable, switched to Secondary"` (or vice versa). **Gotcha:** Only swap on *success* of fallback, not on failure of primary alone.

### FIX-311-03: Restore primary on hourly health check
**Files:** `Tasks/MarvinTask.cs` (modify) or `Services/ProviderHealthCheckService.cs` (create)
**Effort:** S
**What:** At start of Marvin sync (or as separate hourly check), if `ActiveProvider == Secondary`, ping primary's manifest endpoint (`/manifest.json`) with 5s timeout. If 200 OK, set `ActiveProvider = Primary` and log at Info: `"[Failover] Primary restored"`. **Gotcha:** Don't block sync on health check — fire-and-forget or quick timeout.

### FIX-311-04: Change default sync interval to 1 hour
**Files:** `Configuration/PluginConfiguration.cs` (modify), `Configuration/configurationpage.html` (modify if needed)
**Effort:** S
**What:** Change `SyncIntervalHours` default from `6` to `1`. Update any wizard text or tooltips that reference the 6-hour default. Add validation: minimum 15 minutes, maximum 24 hours. **Gotcha:** Existing users keep their configured value; only new installs get new default.

### FIX-311-05: Gap repair verifies upstream before writing .strm
**Files:** `Services/SeriesGapRepairService.cs` (modify)
**Effort:** M
**What:** Before calling `WriteEpisodeStrmAsync`, call `AioStreamsClient.GetSeriesStreamsAsync(imdbId, season, episode)` with 5s timeout. If response is empty or error, skip that episode and log at Debug: `"[GapRepair] Skipping S{s}E{e} — no upstream streams"`. **Gotcha:** Rate limit these checks — add 100ms delay between episodes to avoid hammering AIOStreams. Cap at 50 episodes per repair run.

### FIX-311-06: Admin endpoint to clear no_streams sentinel
**Files:** `Services/AdminService.cs` (modify or create), `Api/AdminEndpoints.cs` (modify)
**Effort:** S
**What:** Add `POST /InfiniteDrive/Admin/ClearSentinel` with body `{"imdbId": "tt1234567"}`. Deletes the failed cache entry for that item from `resolution_cache` table. Returns 200 OK on success, 404 if no sentinel exists. Requires admin auth. **Gotcha:** Only clear `status = 'failed'` entries, not valid cached URLs.

---

## Verification

- [ ] `dotnet build -c Release` — 0 errors, 0 warnings
- [ ] `./emby-reset.sh` succeeds + Discover UI loads
- [ ] Manual test: Set primary to invalid URL, press Play — **Expected:** Playback succeeds via secondary, logs show failover message
- [ ] Manual test: Fix primary URL, wait for Marvin sync (or trigger manually) — **Expected:** Logs show "Primary restored"
- [ ] Manual test: Fresh install — **Expected:** Sync interval defaults to 1 hour in config
- [ ] Manual test: Trigger gap repair on series with missing episode that doesn't exist upstream — **Expected:** Episode skipped, no ghost .strm created
- [ ] Manual test: Play item that returns no streams, then call `/Admin/ClearSentinel`, play again — **Expected:** Fresh resolution attempt (may still fail, but not instant 503)

---

## Completion

- [ ] All tasks done
- [ ] BACKLOG.md updated
- [ ] REPO_MAP.md updated (note ActiveProviderState singleton, new admin endpoint)
- [ ] git commit -m "chore: end sprint 311 — self-healing failover"
