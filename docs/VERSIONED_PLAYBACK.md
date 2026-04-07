---

# EmbyStreams · Versioned Playback Design Spec
**Revision 2 — Default-First Architecture**

---

## 1. Purpose

EmbyStreams presents AIOStreams results in Emby as versioned `.strm` files for each movie or episode.

The design goal is:

> **One thing plays. It plays correctly. Everything else is opt-in.**

This revision codifies the default-first principle across the entire product lifecycle — from first install through ongoing catalog management.

---

## 2. Core Behavioral Principles

### Principle 1 — Safe by default, powerful by choice
The system ships opinionated. The admin does not need to understand debrid, codec matrices, or Emby version mechanics to get a working library. A working library is what they get on day one, automatically.

### Principle 2 — The default is not "Best Available"
"Best Available" is the wrong default in a debrid context. It is aspirational, not reliable. The correct default is the **highest quality that a median client can reliably direct play without configuration.**

That is: **`HD Broad` — 1080p · H.264 · DD+**

### Principle 3 — Adding versions is a deliberate administrative act
Enabling additional quality slots is not a casual toggle. It triggers real work: rehydration of the entire catalog. The admin must understand and confirm this. The UI enforces the conversation.

### Principle 4 — The page describes the thing that plays
Every `.strm` has its own `.nfo`. The default/base file pair represents exactly what presses Play. No abstract stubs.

---

## 3. First-Run Startup Wizard

### Yes — surface the quality profiles in the wizard.

The startup wizard is the right moment to surface quality profiles because:
- The catalog has not yet been hydrated — there is zero cost to changing the selection
- The admin is already in a configuration mindset
- After first hydration, changing profiles has real consequences; before it, it has none
- It is the only moment where all choices are equally cheap

### Wizard structure

```
Step 1 of 4 · Welcome
Step 2 of 4 · Connect AIOStreams
Step 3 of 4 · Stream Quality          ← quality profiles surface here
Step 4 of 4 · Ready
```

---

### Step 3 — Stream Quality (Wizard)

```
─────────────────────────────────────────────────────
EmbyStreams · Stream Quality

How should EmbyStreams present streams to your users?

  ● Simple · One version per title            ← DEFAULT SELECTED
    EmbyStreams picks the best reliable stream
    automatically. Users just press Play.

  ○ Advanced · Multiple versions per title
    Users can choose quality from a dropdown.
    Requires more setup and adds complexity.

─────────────────────────────────────────────────────
Simple mode uses:  1080p · H.264 · Dolby Digital Plus

This works on virtually every device — Apple TV,
Roku, Fire Stick, smart TVs, phones, and browsers.

                              [ Back ]  [ Continue → ]
─────────────────────────────────────────────────────
```

If the admin selects **Advanced**, a secondary panel expands inline — they do not leave this step:

```
─────────────────────────────────────────────────────
Advanced · Choose which versions users can see

  [✓] HD · Broad          1080p H.264 DD+        ← locked on, always required
  [ ] Best Available      Highest quality found
  [ ] 4K · Dolby Vision   Premium DV tier
  [ ] 4K · HDR            Safe 4K default
  [ ] 4K · SDR            4K without HDR
  [ ] HD · Efficient      Smaller 1080p
  [ ] Compact             Low bandwidth

  Default auto-play version:  [ HD · Broad ▼ ]

  ⚠ Each version you enable means EmbyStreams must
    hydrate your entire catalog once per version.
    A large catalog with 4 versions takes roughly
    4× longer to become playable.

  Enabled: 1 / 8

                              [ Back ]  [ Continue → ]
─────────────────────────────────────────────────────
```

### Wizard design rules

| Rule | Behavior |
|---|---|
| Simple mode selected | HD Broad locked in, wizard proceeds |
| Advanced mode, no extra slots checked | equivalent to Simple, wizard proceeds |
| Advanced mode, extra slots checked | warning inline, admin confirms, wizard proceeds |
| HD Broad cannot be unchecked | it is the floor, always present, always enabled |
| Default version dropdown | only shows enabled slots |
| Wizard skipped / plugin reconfigured | settings page enforces same rules post-install |

---

## 4. Post-Install Settings Page

The settings page mirrors the wizard's Advanced panel, but adds explicit change-consequence warnings because the catalog is now live.

```
─────────────────────────────────────────────────────
EmbyStreams · Stream Versions

  [✓] HD · Broad          1080p H.264 DD+        (default) [locked]
  [ ] Best Available      Highest quality found
  [✓] 4K · HDR            Safe 4K default
  [ ] 4K · Dolby Vision   Premium DV tier
  [ ] 4K · SDR            4K without HDR
  [ ] HD · Efficient      Smaller 1080p
  [ ] Compact             Low bandwidth

  Default auto-play version:  [ HD · Broad ▼ ]

  Enabled: 2 / 8

  [ Save Changes ]

─────────────────────────────────────────────────────
```

### Change consequence warnings

These fire **before** Save is confirmed, not after.

#### Adding a version slot

```
┌─────────────────────────────────────────────────────┐
│  Add 4K · HDR to all titles?                        │
│                                                     │
│  EmbyStreams will rehydrate your entire catalog     │
│  to add this version. Your library stays playable  │
│  during this process — existing versions are not   │
│  removed until new ones are confirmed.              │
│                                                     │
│  Estimated time: ~2 hours for 1,200 titles          │
│  (based on your current catalog size)               │
│                                                     │
│            [ Cancel ]   [ Add Version → ]           │
└─────────────────────────────────────────────────────┘
```

#### Removing a version slot

```
┌─────────────────────────────────────────────────────┐
│  Remove 4K · HDR from all titles?                   │
│                                                     │
│  EmbyStreams will remove all 4K · HDR files from   │
│  your library immediately. This cannot be undone   │
│  without re-enabling and rehydrating.               │
│                                                     │
│            [ Cancel ]   [ Remove Version → ]        │
└─────────────────────────────────────────────────────┘
```

#### Changing the default version

```
┌─────────────────────────────────────────────────────┐
│  Change default to 4K · HDR?                        │
│                                                     │
│  The base filename for every title will be          │
│  rewritten to represent 4K · HDR. Users pressing   │
│  Play without choosing a version will get 4K · HDR.│
│                                                     │
│  This rewrites files for all titles. Your library  │
│  stays playable during the transition.             │
│                                                     │
│            [ Cancel ]   [ Change Default → ]        │
└─────────────────────────────────────────────────────┘
```

---

## 5. Rehydration Behavior

### What triggers rehydration

| Admin action | Rehydration scope |
|---|---|
| Enable a new version slot | Full catalog — add new slot files |
| Disable a version slot | Full catalog — remove slot files immediately |
| Change default version | Full catalog — rewrite base filename pairs |
| No change to slots or default | No rehydration triggered |

### Rehydration is non-destructive during the run

When adding a new slot:
1. Existing `.strm` / `.nfo` pairs for all other slots remain on disk and remain playable
2. New slot files are written incrementally as candidates are confirmed
3. If a title has no viable candidate for the new slot, no file is written for that title — the slot is silently absent for that title only
4. No Emby library scan is forced mid-run — a single scan is triggered on completion

When removing a slot:
1. Files for that slot are removed immediately across the catalog
2. An Emby library scan is triggered to remove the items from the Emby index
3. This is the one destructive operation — warn clearly

When changing the default:
1. The new default slot's files already exist on disk (it must be an enabled slot)
2. The base filename pair is rewritten to point to the new default slot
3. The old default slot's suffixed pair is written to take its place
4. Net file count per title does not change

### Rehydration respects trickle rate limits
Rehydration runs through the existing trickle hydration pipeline. It does not bypass indexer rate limiting. Large catalogs will take time. The UI communicates this honestly.

---

## 6. Slot Definitions and Audio Policy

### Slot catalog

| Slot Key | Label | Resolution | Video | HDR | Audio preference order |
|---|---|---|---|---|---|
| `hd_broad` | HD · Broad | 1080p | H.264 | none | DD+ 5.1 → DD 5.1 → AAC stereo |
| `best_available` | Best Available | highest found | any | any | Atmos → DD+ 7.1 → DD+ 5.1 → DD 5.1 |
| `4k_dv` | 4K · Dolby Vision | 2160p | HEVC/AV1 | DV | Atmos → DD+ 7.1 → DD+ 5.1 |
| `4k_hdr` | 4K · HDR | 2160p | HEVC/AV1 | HDR10 | Atmos → DD+ 5.1 → DD 5.1 |
| `4k_sdr` | 4K · SDR | 2160p | HEVC/AV1 | none | DD+ 5.1 → DD 5.1 → AAC |
| `hd_efficient` | HD · Efficient | 1080p | HEVC | none | DD+ 5.1 → AAC stereo |
| `compact` | Compact | 720p or lower | H.264 | none | AAC → DD |

### HD Broad is the permanent floor
- It cannot be disabled
- It is always the fallback default if the configured default slot is later disabled
- It is always present in the dropdown on the settings page

### Audio is internal ranking, not a user-visible axis
Users never choose audio. Audio preference is a slot-level policy. It is applied at candidate ranking time, invisible to the user.

---

## 7. Filesystem Layout

Unchanged from Revision 1. Repeated here for completeness.

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

- Version suffix is the slot **Label**, verbatim: `4K · HDR` → `4K HDR` (replace `·` with space, trim)
- Base pair has no suffix — it is always the current default slot
- Suffixed pairs represent all other enabled slots

---

## 8. `.strm` Content

Each `.strm` contains the plugin's stream resolution endpoint URL, written at materialization time using the Emby server's accessible LAN address:

```
http://[emby-server-lan-ip]:[port]/EmbyStreams/play?titleId=tt123456&slot=hd_broad&token=[api-token]
```

- URL is written at materialization time
- If server address changes, a re-materialization sweep is triggered on plugin startup
- The endpoint performs just-in-time candidate resolution and returns `302` to the live debrid URL
- No debrid URL ever touches disk

---

## 9. NFO Strategy

Each `.nfo` contains:

### Shared title metadata
- title, year, plot
- TMDB / IMDb IDs
- genres, cast, crew, runtime

### Version-specific technical metadata (synthetic, calculated from AIOStreams payload)
- resolution
- video codec
- HDR / DV class
- audio codec and channels
- source type
- `<streamdetails>` block

Synthetic `<streamdetails>` is marked in code commentary as derived, not probed. It exists for Emby display polish, not for algorithmic version selection.

---

## 10. Playback Flow

Unchanged from Revision 1.

1. User presses Play (or selects a version from dropdown)
2. Plugin endpoint receives request for `titleId` + `slot`
3. Check ephemeral playback URL cache for this slot
4. If valid → `302` immediately
5. If missing/stale → resolve from slot's candidate ladder
6. Cache resolved URL briefly
7. If primary candidate fails → fall back to next candidate in ladder
8. If all candidates fail → refresh AIOStreams snapshot for this title and retry once
9. If still failing → return a clean error; do not attempt transcode

---

## 11. Implementation Baseline

| Topic | Settled decision |
|---|---|
| First-run default | HD Broad (1080p H.264 DD+), single version, auto-selected |
| HD Broad slot | Permanent floor — cannot be disabled, always present |
| Additional slots | Admin opt-in, surfaced in wizard and settings page |
| Wizard quality step | Yes — surfaces Simple vs Advanced choice before first hydration |
| Post-install slot changes | Trigger full catalog rehydration with explicit confirmation dialogs |
| Default version change | Triggers base filename rewrite across catalog with confirmation |
| Rehydration | Non-destructive for additions; immediate file removal for deletions |
| `.strm` content | Plugin endpoint URL (LAN address), never debrid URL |
| NFO streamdetails | Synthetic, calculated, for display only |
| Audio | Slot-level ranking policy, never user-visible |
| Version limit | Maximum 8 enabled/materialized slots per title |
| Emby version picker | Escape hatch for power users, not primary UX |
| Fail behavior | Fast, clean error — never silent transcode attempt |
