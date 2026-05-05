# Stream Resolution Pipeline

Stream resolution is the process that turns a library item (a `.strm` file in Emby) into a playable CDN URL. This document describes the current pipeline from cache lookup through live resolution to playback.

## 1. Resolution Flow Overview

When a user browses or plays an item, the pipeline follows this order:

1. **Pre-cache check** via `StreamCacheService` against `stream_resolution_cache`
2. **Live resolution** via `AioMediaSourceProvider.GetMediaSources()` on cache miss
3. **OpenMediaSource gating** via `RequiresOpening=true`
4. **Fire-and-forget cache write** after live resolve

## 2. Pre-Cache Check

When `AioMediaSourceProvider.GetMediaSources()` is called:

1. Query `StreamCacheService` for cached entries in `stream_resolution_cache` matching the item's `aio_id` (and `season`/`episode` for series).
2. **HIT** — `BuildMediaSources()` converts cached entries into `MediaSourceInfo[]` instantly (<500ms). Each source has `Path=""`, `RequiresOpening=true`, and an `OpenToken` encoding `infoHash + fileIdx`.
3. **MISS** — falls through to live resolution.

Cached entries are populated by `PreCacheAioStreamsTask` (background) and by fire-and-forget writes after live resolution.

## 3. Live Resolution

When the cache has no entry for an item:

1. `AioMediaSourceProvider` calls `AioStreamsClient.GetStreamsAsync()` with the item's provider ID.
2. The AIOStreams API returns available streams from configured debrid providers.
3. Streams are scored by quality tier and provider priority, then ranked.
4. The top-ranked streams become `MediaSourceInfo[]` with `RequiresOpening=true`.
5. Stream data is written to `stream_resolution_cache` via fire-and-forget `ProbeAndCacheAsync` for future lookups.

### Multi-Manifest Failover

If the primary manifest (`PrimaryManifestUrl`) returns `ProviderDown` or `ContentMissing`, the system attempts resolution against the secondary manifest (`SecondaryManifestUrl`). An item is only considered "dead" if both manifests return `ContentMissing`.

## 4. OpenMediaSource Flow

All playback sources have `RequiresOpening=true`. Emby never plays a `Path` directly — it always calls `OpenMediaSource(openToken)`.

### Pre-Cached Stream

1. `OpenMediaSource()` deserializes the `CachedStreamOpenToken` from the open token.
2. Tries the cached URL directly (no HEAD check on rank-0).
3. If the cached URL is expired: re-resolves via AIOStreams using `infoHash + fileIdx` matching against a fresh stream response.
4. Returns `InfiniteDriveLiveStream` with a fresh CDN URL.

### Live-Resolved Stream

1. `OpenMediaSource()` tries candidates in rank order.
2. For rank-0 candidates: skips the HEAD probe and goes directly to playback (fastest path).
3. For lower-ranked candidates: falls back through `BuildFallbackStreamsFromFilename` if higher ranks fail.
4. Returns `InfiniteDriveLiveStream` with the resolved CDN URL.

### Fallback Strategy

If the primary candidate fails during `OpenMediaSource`:

1. Try next rank-ordered candidate.
2. Use `BuildFallbackStreamsFromFilename` to construct alternatives from the filename metadata.
3. If all candidates fail, attempt a fresh AIOStreams resolve.
4. On `Throttled` (429): serve the best cached variant available rather than returning an error to the client.

## 5. Rank-Based Stream Selection

Streams are ranked by:

1. **Quality tier** — 4K > 1080p > 720p > SD. Per-tier source limits (`MaxStreams*`) cap how many streams each tier contributes; `DefaultQualityTier` sets the fallback.
2. **Provider priority** — within the same tier, earlier providers in `ProviderPriorityOrder` win.
3. **Language match** — audio language matching the user's `PreferredMetadataLanguage` is preferred.

The rank-0 stream is the "best" match. It gets special treatment: no HEAD probe during `OpenMediaSource`, ensuring the fastest possible playback start.

## 6. Stream Identity

The durable identity of a stream is `infoHash + fileIdx`. This pair:

* Survives CDN URL rotation (the hash stays the same even as CDN URLs expire).
* Is encoded in open tokens for re-resolution.
* Allows matching against fresh AIOStreams responses to find the same file with a new URL.

## 7. CooldownGate Throttling

All AIOStreams HTTP calls pass through `CooldownGate.WaitAsync()` with one of two `CooldownKind` profiles:

| CooldownKind | Purpose | Delay (Shared) | Delay (Private) |
|---|---|---|---|
| `Default` | General API calls, catalog fetch, stream resolve | 0ms | 0ms |
| `SeriesMeta` | Series metadata expansion, episode lookups | 200ms | 0ms |

On any 429 response:

1. `CooldownGate.Tripped()` sets a global cooldown (60s shared, 30s private).
2. All HTTP pauses until the cooldown expires.
3. If 3+ 429s occur within 1 hour on a shared instance, the dashboard suggests switching to a private instance.

See [COOLDOWN.md](COOLDOWN.md) for full details.

## 8. ResolutionResult Statuses

All resolution attempts return a `ResolutionResult` object (never null):

| Status | Meaning | Action |
|---|---|---|
| **Success** | URL found and validated | Play immediately / update cache |
| **Throttled** | Provider returned 429 | Cease attempts for this item; retry in next cycle |
| **ProviderDown** | Provider 5xx or connection timeout | Trigger failover to secondary manifest |
| **ContentMissing** | Provider returned 404 | Check other manifest; if both 404, mark for deletion |

## 9. Pre-Cache Background Task

`PreCacheAioStreamsTask` runs inside MarvinTask on a configurable interval:

* **Interval:** `PreCacheIntervalHours` (default 6h, range 1-48h)
* **Batch size:** `PreCacheBatchSize` (default 42, range 1-500)
* **TTL:** `PreCacheTTLDays` (default 14 days, range 1-90)
* Budget-gated: checks `IsBudgetExhaustedAsync()` before each item
* Rate-limit aware: respects CooldownGate on 429 responses
* Provider failover: tries all configured providers per item via `ResolverHealthTracker`

## 10. Language-Aware Resolution

When multiple cached candidates exist, `ResolverService` applies a language fallback chain:

1. Parse `X-Emby-Token` from request headers via `IAuthorizationContext`.
2. Read the user's `PreferredMetadataLanguage`.
3. If empty, fall back to `Config.DefaultSubtitleLanguage` (global plugin setting).
4. If candidates have different `Languages` fields, prefer those matching the resolved language.
5. Falls through to rank-order selection if no language match found.

### MediaStreams (Version Picker)

`AioMediaSourceProvider` populates `MediaSourceInfo.MediaStreams` for each stream:

**Audio streams** — built from `AioStreamsStream.ParsedFile`:
* One `MediaStream` per language in `ParsedFile.Languages`
* Title format: `"{language} - {channels} {audioTags}"` (e.g. "Japanese - 5.1 Atmos")
* First language marked `IsDefault = true`

**Subtitle streams** — built from `AioStreamsStream.Subtitles`:
* One `MediaStream` per subtitle entry
* `IsExternal = true`, `DeliveryUrl` and `Path` set to subtitle URL
* Language from `subtitle.Lang`

**Language sorting** — sources sorted by: `PluginConfiguration.DefaultSubtitleLanguage` → library's `PreferredMetadataLanguage` (via `ILibraryManager.GetVirtualFolders()`) → no sort. Matching audio streams marked `IsDefault = true`.
