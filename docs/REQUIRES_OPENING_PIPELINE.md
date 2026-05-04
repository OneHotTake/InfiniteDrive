# RequiresOpening Pipeline — Secure On-Demand Stream Resolution

## Problem

Every `.strm` file contains a signed resolve URL pointing to `/InfiniteDrive/Resolve` (unauthenticated endpoint). When `GetMediaSources()` fails or Emby bypasses it, the `.strm` URL is played directly — triggering HMAC token validation, rate limiting, and exposing the resolve pipeline without Emby auth.

## Fix

`MediaSourceInfo.RequiresOpening = true` prevents Emby from ever playing a `Path` directly. Emby ALWAYS calls `OpenMediaSource()`, which is gated by Emby's auth layer. The `.strm` file becomes a dumb library placeholder.

## Playback Flow

1. User clicks Play -> Emby calls `GetMediaSources()` on `AioMediaSourceProvider`
2. Provider returns sources with `RequiresOpening = true`, `Path = ""`, `OpenToken = <encoded token>`
3. User selects version (or Emby auto-selects) -> Emby calls `OpenMediaSource(openToken)`
4. `OpenMediaSource()` validates the token -> resolves CDN URL -> returns `InfiniteDriveLiveStream`
5. Emby plays from `InfiniteDriveLiveStream.MediaSource.Path` (CDN URL)

CDN URLs never appear in `.strm` content or `MediaSourceInfo.Path` during picker display.

## Key Components

| Component | Role |
|-----------|------|
| `AioMediaSourceProvider.GetMediaSources()` | Checks cache, live resolves if miss; sets `RequiresOpening=true`, `OpenToken`, `Path=""` |
| `AioMediaSourceProvider.OpenMediaSource()` | Validates token, resolves fresh CDN URL, returns `InfiniteDriveLiveStream` |
| `Models/InfiniteDriveLiveStream.cs` | `ILiveStream` wrapper carrying resolved `MediaSourceInfo` |
| `StreamCacheService` | Reads/writes `stream_resolution_cache`; builds `MediaSourceInfo[]` from cached entries |
| `StrmWriterService` | Calls `ILibraryMonitor.ReportFileSystemChanged()` after write |

## OpenMediaSource Internals

### Pre-Cached Stream (from `stream_resolution_cache`)

When `AioMediaSourceProvider` serves a pre-cached item:

1. `StreamCacheService` returns cached entries from `stream_resolution_cache`.
2. `BuildMediaSources()` creates `MediaSourceInfo[]` with `RequiresOpening=true`.
3. Each source's `OpenToken` = JSON-serialized `CachedStreamOpenToken`:
   ```
   { infoHash, fileIdx, imdbId, season, episode, mediaType, url, headersJson, providerName }
   ```
4. User selects version -> Emby calls `OpenMediaSource(openToken)`.
5. `OpenFromCachedTokenAsync()`:
   - Skips HEAD probe on rank-0 (fastest playback path).
   - If cached URL is expired: re-resolves via AIOStreams using `infoHash + fileIdx` matching against fresh `/stream/{type}/{id}.json` response.
   - Returns `InfiniteDriveLiveStream` with fresh CDN URL.

Pre-cached streams never rely on M3U8 probing. The durable `infoHash + fileIdx` identity allows fresh URL resolution even after CDN URL rotation.

### Live-Resolved Stream

When no cache entry exists:

1. `GetMediaSources()` calls AIOStreams live.
2. Streams are scored and ranked by quality tier and provider priority.
3. Rank-0 stream skips HEAD probe during `OpenMediaSource` for instant playback.
4. Lower-ranked streams serve as fallback if rank-0 fails.
5. `BuildFallbackStreamsFromFilename` constructs alternatives from filename metadata if all ranked candidates fail.
6. After successful live resolve: fire-and-forget `ProbeAndCacheAsync` writes results to `stream_resolution_cache` for future lookups.

### Candidate Fallback Chain

During `OpenMediaSource`, if the primary candidate fails:

1. Try next candidate in rank order (no HEAD probe on rank-0).
2. Use `BuildFallbackStreamsFromFilename` for filename-based alternatives.
3. If all candidates fail, attempt fresh AIOStreams resolve.
4. On `Throttled` (429): serve best available cached variant rather than erroring.

## Binge Prefetch

`BingePrefetchService` pre-loads the next episode. When Emby auto-plays next episode:

1. `GetMediaSources()` -> cache hit -> instant decorated sources (no AIOStreams call).
2. Single source -> Emby auto-plays, calls `OpenMediaSource()` -> instant return.
3. Multiple -> user sees picker.

No special logic needed.

## Rollback

`PluginConfiguration.UseRequiresOpening` (bool, default `true`). Set to `false` to revert to CDN-URL-in-Path behavior without redeploy. In practice, this is always `true` and the setting exists for emergency rollback only.
