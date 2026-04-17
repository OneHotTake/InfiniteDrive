# Single-Stream Binary Proxy with SEL Fallback

## Problem

The M3U8 master playlist approach is fundamentally broken with Emby's transcoding pipeline:

1. `/InfiniteDrive/resolve` returns M3U8 text with variant URLs pointing to binary MP4/MKV files
2. ffprobe cannot parse this as media input (it's not a valid HLS segment playlist)
3. Without stream info, Emby launches ffmpeg with `-vn -an -sn` producing empty `.ts` segments
4. Resolution takes ~7s (20 streams fetched, filtered, probed) — client disconnects before response

Additionally, probing multiple upstream streams hits real-debrid CDNs aggressively (rate limiting, IP bans).

## Solution

Replace the M3U8 architecture with a single-stream binary proxy using AIOStreams' Stream Expression Language (SEL).

### Core Flow

```
.strm → /InfiniteDrive/Resolve?token=...&quality=hd_broad&id=tt32141377
                    |
        ResolverService.Get():
          1. Cache lookup: {id + quality → aioPlaybackUrl, servedTier}
             HIT  → 302 redirect to /Stream?url={signed_url} (instant)
             MISS → continue to step 2
          2. SEL fallback chain:
             Try "hd_broad" SEL → got stream? → done
             Try "sd_broad" SEL → got stream? → done
             Try "slice(streams,0,1)" → last resort
          3. Cache the resolved AIOStreams playback URL (forever)
          4. 302 redirect to /Stream?url={signed_url}
                    |
        /InfiniteDrive/Stream?url={signed_url}
          1. Validate HMAC signature
          2. Follow 307 redirect to CDN
          3. Proxy binary MP4/MKV to caller
          4. Forward Range headers (seeking)
          5. Set Content-Type: video/mp4
                    |
        ffprobe → gets binary MP4 → parses codec/resolution/duration OK
        ffmpeg  → gets binary MP4 → transcodes for potato clients OK
```

### SEL Expressions

| Quality Tier | SEL Expression |
|---|---|
| `4k_hdr` | `slice(resolution(visualTag(streams,'DV','HDR','HDR10+'),'2160p'),0,1)` |
| `4k_sdr` | `slice(resolution(streams,'2160p'),0,1)` |
| `hd_broad` | `slice(resolution(streams,'1080p'),0,1)` |
| `sd_broad` | `slice(resolution(streams,'720p','480p'),0,1)` |
| `any` (last resort) | `slice(streams,0,1)` |

### Fallback Chain

| Requested | Fallback Order |
|---|---|
| `4k_hdr` | 4k_hdr, 4k_sdr, hd_broad, sd_broad, any |
| `4k_sdr` | 4k_sdr, hd_broad, sd_broad, any |
| `hd_broad` | hd_broad, sd_broad, any |
| `sd_broad` | sd_broad, any |

Each step is one AIOStreams HTTP call. Stop on first hit. Log mismatches.

### Cache Design

**Key**: `{imdb_id}:{quality_tier}` (e.g. `tt32141377:hd_broad`)
**Value**: `{aioPlaybackUrl, servedTier, resolvedAt}`

- **No expiry** — cache the AIOStreams playback URL (the long hashed URL) forever
- **Invalidation** — on next play, the `/Stream` proxy follows the 307. If CDN returns 403/410/5xx, mark stale and re-resolve through the fallback chain
- The CDN URL (307 target) is volatile and short-lived — never cache it, always follow the 307 fresh

### Design Decisions

1. **No SEL fallback for unsupported instances** — SEL is standard on all AIOStreams instances
2. **Respect quality tier from .strm** — the quality param drives the SEL expression
3. **Cache the AIOStreams playback URL forever** — not the CDN resolution (volatile)
4. **No M3U8** — resolve returns 302 redirect to `/Stream`, which proxies binary content
5. **Emby's built-in quality selector** handles transcode bitrate for potato clients

### What Gets Removed

- `Services/M3u8Builder.cs` — entire file
- `Models/M3U8Variant` — embedded in M3u8Builder
- `ResolverService`: ProbeAndReorderAsync, FilterStreamsWithFallback, SelectTopStreams, BuildPlaylist, BuildPlaylistFromCandidates, MapTierToResolution, EstimateBandwidth, WriteToCacheAsync (multi-candidate)
- `StreamEndpointService`: M3U8 rewrite path (RewriteHlsUrls, IsM3U8Request, GetBaseUrl)

### What Gets Changed

- `AioStreamsClient`: Add optional `sel` parameter to GetMovieStreamsAsync/GetSeriesStreamsAsync
- `ResolverService`: Replace M3U8 logic with SEL fallback chain → 302 redirect
- `StreamEndpointService`: Binary proxy only
- DB cache: Simplified shape — single URL per {id, quality} instead of multiple candidates
- `ResolverRequest` route summary update (no longer "return M3U8 manifest")

### Quality Selection

- The .strm file contains the quality tier (e.g., `quality=hd_broad`)
- Emby's built-in transcode quality selector handles bitrate reduction for potato clients
- Future: multiple .strm files per item at different quality tiers (Emby "versions" feature)
