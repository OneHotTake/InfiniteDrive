# Versioned Playback

**Current revision — Default-First Architecture**

---

## 1. Purpose

InfiniteDrive presents AIOStreams results in Emby as versioned `.strm` files per quality slot for each movie or episode.

**Design goal:** One thing plays. It plays correctly. Everything else is opt-in.

---

## 2. Core Behavioral Principles

### Principle 1 — Safe by default, powerful by choice
The system ships opinionated. HD Broad (1080p H.264 DD+) plays on virtually every device — Apple TV, Roku, Fire Stick, smart TVs, phones, browsers.

### Principle 2 — The default is not "Best Available"
"Best Available" is aspirational, not reliable. The correct default is the **highest quality that a median client can reliably direct play without configuration.**

That is: **`hd_broad` — 1080p · H.264 · DD+**

### Principle 3 — Adding versions is a deliberate administrative act
Enabling additional quality slots triggers rehydration of the catalog. The UI surfaces this consequence clearly.

### Principle 4 — The page describes the thing that plays
Every `.strm` has its own `.nfo`. The default/base file pair represents exactly what presses Play.

---

## 3. Slot Definitions

| Slot Key | Label | Resolution | Video | HDR | Audio preference |
|---|---|---|---|---|---|
| `hd_broad` | HD · Broad | 1080p | H.264 | none | DD+ 5.1 → DD 5.1 → AAC stereo |
| `best_available` | Best Available | highest found | any | any | Atmos → DD+ 7.1 → DD+ 5.1 |
| `4k_hdr` | 4K · HDR | 2160p | HEVC/AV1 | HDR10 | Atmos → DD+ 5.1 → DD 5.1 |
| `4k_dv` | 4K · Dolby Vision | 2160p | HEVC/AV1 | DV | Atmos → DD+ 7.1 → DD+ 5.1 |
| `4k_sdr` | 4K · SDR | 2160p | HEVC/AV1 | none | DD+ 5.1 → DD 5.1 → AAC |
| `hd_efficient` | HD · Efficient | 1080p | HEVC | none | DD+ 5.1 → AAC stereo |
| `compact` | Compact | ≤720p | H.264 | none | AAC → DD |

### `hd_broad` is the permanent floor
- Cannot be disabled
- Always the fallback default if the configured default is later disabled
- Always present in the dropdown

### Audio is internal ranking, not a user-visible axis
Users never choose audio. Audio preference is a slot-level policy applied at candidate ranking time, invisible to the user.

---

## 4. Filesystem Layout

### Movies

```
/Movies/
  Avatar Fire and Ash (2025)/
    Avatar Fire and Ash (2025).strm          ← base pair = default slot
    Avatar Fire and Ash (2025).nfo
    Avatar Fire and Ash (2025) - 4K HDR.strm
    Avatar Fire and Ash (2025) - 4K HDR.nfo
    poster.jpg
    backdrop.jpg
```

### Series

```
/TV/
  Show Name/
    tvshow.nfo
    Season 01/
      Show Name - S01E01.strm               ← base pair = default slot
      Show Name - S01E01.nfo
      Show Name - S01E01 - 4K HDR.strm
      Show Name - S01E01 - 4K HDR.nfo
```

### Naming rules

- Version suffix is the slot **Label**: `4K · HDR` → `4K HDR` (replace `·` with space, trim)
- Base pair has no suffix — always the current default slot
- Suffixed pairs represent all other enabled slots

---

## 5. `.strm` Content

Each `.strm` contains the plugin's resolution endpoint URL, written at materialization time:

```
http://127.0.0.1:8096/InfiniteDrive/Resolve?id=tt1234567&quality=hd_broad&token=base64sig|1700000000
```

- Written at materialization time using `EmbyBaseUrl`
- If server address changes, re-materialization is needed (re-sync)
- `PluginSecret` signs the token; `PlaybackTokenService.ValidateStreamToken` verifies it
- `SignatureValidityDays` controls token lifetime (default: 365 days)

---

## 6. Playback Flow

```
1. User presses Play (or selects a version from dropdown)
2. Emby reads .strm → requests /InfiniteDrive/Resolve?id=tt123&quality=hd_broad&token=...
3. ResolverService validates HMAC token via PlaybackTokenService
4. Check ephemeral playback URL cache for this slot
5. If valid → 302 to CDN URL immediately
6. If missing/stale → resolve from slot's candidate ladder
7. Cache resolved URL briefly
8. If primary candidate fails → fall back to next candidate in ladder
9. If all candidates fail → refresh AIOStreams snapshot and retry once
10. If still failing → return clean error; do not attempt transcode
```

### M3U8 Construction
`M3u8Builder` assembles a ranked M3U8 playlist:
- Candidates ranked by quality tier (4K > 1080p > 720p, etc.)
- Within the same tier, ranked by provider priority (`ProviderPriorityOrder`)
- Dead streams sink to the bottom as player fallback
- `StreamProbeService` validates URLs before including them in the playlist

---

## 7. Rehydration Behavior

| Admin action | Scope |
|---|---|
| Enable a new version slot | Full catalog — add new slot files |
| Disable a version slot | Full catalog — remove slot files immediately |
| Change default version | Full catalog — rewrite base filename pairs |
| No change | No rehydration |

### Non-destructive additions
1. Existing `.strm` / `.nfo` pairs remain on disk and remain playable
2. New slot files written incrementally as candidates are confirmed
3. No Emby library scan forced mid-run — single scan on completion

### Immediate removal
1. Files for the disabled slot are removed immediately
2. An Emby library scan is triggered to remove items from the Emby index

### Default change
1. The new default slot's files already exist (must be enabled)
2. The base filename pair is rewritten to point to the new default slot
3. Net file count per title does not change

---

## 8. NFO Strategy

Each `.nfo` is written by `NfoWriterService`:

### Seed NFO (written at materialization)
- `<uniqueid type="imdb">tt...</uniqueid>`
- `<uniqueid type="tmdb">xxx</uniqueid>`
- No plot, poster, or cast data

### Enriched NFO (written by MarvinTask / MetadataFallbackTask)
- title, year, plot, genres, cast, director
- TMDB / IMDb IDs
- `<streamdetails>` block (synthetic, for Emby display polish)

---

## 9. Configuration

### `DefaultSlotKey`
Which slot plays when the user presses Play without choosing. Default: `hd_broad`.

### `CandidatesPerProvider`
How many ranked candidates stored per debrid provider per item. Default: `3`.

### `CandidateTtlHours`
Hours before a normalized candidate expires and is cleaned up. Default: `6`.

### `PendingRehydrationOperations`
JSON queue of pending slot add/remove/rename operations. Consumed by `RehydrationTask`.

---

## 10. Implementation Baseline

| Topic | Decision |
|---|---|
| First-run default | `hd_broad` (1080p H.264 DD+), single version, auto-selected |
| HD Broad slot | Permanent floor — cannot be disabled |
| Additional slots | Admin opt-in |
| Rehydration | Non-destructive for additions; immediate file removal for deletions |
| `.strm` content | `/InfiniteDrive/Resolve` endpoint URL with HMAC token, never CDN URL |
| NFO streamdetails | Synthetic, calculated, for display only |
| Audio | Slot-level ranking policy, not user-visible |
| Fail behavior | Fast, clean error — never silent transcode attempt |
