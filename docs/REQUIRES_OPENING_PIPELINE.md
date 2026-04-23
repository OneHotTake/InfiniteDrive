# RequiresOpening Pipeline — Secure On-Demand Stream Resolution

## Problem

Every `.strm` file contains a signed 365-day resolve URL pointing to `/InfiniteDrive/Resolve` (unauthenticated endpoint). When `GetMediaSources()` fails or Emby bypasses it, the `.strm` URL is played directly — triggering HMAC token validation, rate limiting, and exposing the resolve pipeline without Emby auth.

## Fix

`MediaSourceInfo.RequiresOpening = true` prevents Emby from ever playing a `Path` directly. Emby ALWAYS calls `OpenMediaSource()`, which is gated by Emby's auth layer. The `.strm` file becomes a dumb library placeholder.

## Playback Flow

1. User clicks Play → Emby calls `GetMediaSources()` on `AioMediaSourceProvider`
2. Provider returns sources with `RequiresOpening = true`, `OpenToken = cdnUrl`
3. User selects version (or Emby auto-selects) → Emby calls `OpenMediaSource(openToken)`
4. `OpenMediaSource()` validates token is a real CDN URL → returns `InfiniteDriveLiveStream`
5. Emby plays from `InfiniteDriveLiveStream.MediaSource.Path` (CDN URL)

CDN URLs never appear in `.strm` content or `MediaSourceInfo.Path` during picker display.

## Key Components

| Component | Role |
|-----------|------|
| `AioMediaSourceProvider.GetMediaSources()` | Sets `RequiresOpening=true`, `OpenToken=cdnUrl`, `Path=""` |
| `AioMediaSourceProvider.OpenMediaSource()` | Validates token → returns `InfiniteDriveLiveStream` |
| `Models/InfiniteDriveLiveStream.cs` | `ILiveStream` wrapper carrying resolved `MediaSourceInfo` |
| `StrmWriterService` | Calls `ILibraryMonitor.ReportFileSystemChanged()` after write |

## Deprecated (Follow-up Sprint)

- `Services/ResolverService.cs` — `/InfiniteDrive/Resolve` endpoint
- `Services/StreamEndpointService.cs` — `/InfiniteDrive/Stream` endpoint
- `PlaybackTokenService.GenerateResolveToken()` / `ValidateStreamToken()`
- `PluginConfiguration.DefaultSlotKey`, `SignatureValidityDays`

## Rollback

`PluginConfiguration.UseRequiresOpening` (bool, default `true`). Set to `false` to revert to CDN-URL-in-Path behavior without redeploy. Old Resolve/Stream endpoints remain running as fallback for pre-existing `.strm` files.

## Binge Watching

`BingePrefetchService` pre-loads next episode. When Emby auto-plays next episode:
1. `GetMediaSources()` → DB hit → instant decorated sources (no AIOStreams call)
2. Single source → Emby auto-plays, calls `OpenMediaSource()` → instant return
3. Multiple → user sees picker

No special logic needed.
