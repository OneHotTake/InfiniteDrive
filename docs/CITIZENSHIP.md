# The Corrected Citizenship Model

The real files — your Curated physical library — are **sovereign territory**. EmbyStreams doesn't touch them, doesn't manage them, doesn't know they exist beyond "don't go there." They're Citizens by birthright, not by promotion. EmbyStreams only manages what it creates.

```
SOVEREIGN  = Real physical files. Your Curated library.
             Out of scope. EmbyStreams defers unconditionally.

CITIZEN    = Pinned .strm in the Permanent library.
             User explicitly chose this. Survives all syncs.
             EmbyStreams manages it, but user owns the relationship.

RESIDENT   = Unpinned .strm in the Permanent library.
             Got here via interaction. AIO-feed-backed.
             Survives sync unless feed drops it AND user never engaged.

GHOST      = .strm in the IID hidden library.
             Ephemeral. Searchable but invisible on home screen.
             Promoted on interaction. Pruned by scheduler if untouched.
```

The progression:

```
GHOST --(interaction)--> RESIDENT --(explicit intent)--> CITIZEN
                              |                               |
                         (sync prunes)                 (never pruned)
                              |
                         back to GHOST
                         or deleted

                                    SOVEREIGN exists independently.
                                    All others defer to it.
```

---

## How SOVEREIGN Changes Everything

The SOVEREIGN check is the **zeroth step** in every operation. Before writing a Ghost, before promoting a Resident, before anything:

```
IS THIS TMDB ID SOVEREIGN?
  YES -> Stop. Do nothing. Log it. Move on.
  NO  -> Continue with normal flow.
```

SOVEREIGN is determined by a reconciliation job that runs:
- At plugin install (one-time full scan)
- On a weekly schedule (catch new Curated additions)
- On demand via admin trigger

It walks every non-EmbyStreams Emby library, extracts TMDB IDs from existing metadata/NFOs, and writes them into your DB as `citizenship = 'sovereign'`. This table is **read-only from EmbyStreams' perspective** — only the reconciliation job writes it.

**Critical rule:** If a SOVEREIGN record appears for something currently RESIDENT or CITIZEN, immediately delete the EmbyStreams `.strm` and mark `superseded_by_sovereign = true`. The real file is better. Get out of its way.

---

## The Full State Machine

```
+---------------------------------------------------------------------+
|                                                                     |
|   +===========+   <- Real files. Curated library. Beyond scope.    |
|   | SOVEREIGN |      EmbyStreams never writes here.                 |
|   +====+======+      All other states defer unconditionally.        |
|        |                                                            |
|        | (reconciliation detects real file)                         |
|        | <- if RESIDENT/CITIZEN exists, .strm deleted               |
|        |                                                            |
|   +----+--------------------------------------------------------+   |
|   |              EMBYSTREAMS MANAGED ZONE                       |   |
|   |                                                             |   |
|   |   +---------+                                               |   |
|   |   |  GHOST  |  <- IID hidden library                       |   |
|   |   |         |     .strm + NFO                              |   |
|   |   |         |     Searchable, not on home screen           |   |
|   |   +----+----+                                               |   |
|   |        |                                                    |   |
|   |        | user clicks Play (first interaction)              |   |
|   |        v                                                    |   |
|   |   +---------+                                               |   |
|   |   |RESIDENT |  <- Permanent library                        |   |
|   |   |         |     .strm + NFO                              |   |
|   |   |         |     Visible on home screen                   |   |
|   |   |         |     AIO feed still backing it                |   |
|   |   |         |     Prunable if: feed drops it               |   |
|   |   |         |               AND unwatched                  |   |
|   |   |         |               AND not favorited              |   |
|   |   +----+----+                                               |   |
|   |        |                                                    |   |
|   |        | watch >50% OR favorite OR explicit Add            |   |
|   |        v                                                    |   |
|   |   +---------+                                               |   |
|   |   | CITIZEN |  <- Permanent library                        |   |
|   |   |         |     .strm + NFO                              |   |
|   |   |         |     Pinned. Never pruned by sync.            |   |
|   |   |         |     User owns this relationship.             |   |
|   |   +---------+                                               |   |
|   +-------------------------------------------------------------+   |
+---------------------------------------------------------------------+
```

---

## Resolution Hierarchy Per State

**SOVEREIGN**
```
Emby native playback -> real file on disk.
EmbyStreams not involved. At all.
```

**CITIZEN / RESIDENT (Permanent library)**
```
Emby reads .strm
  -> GET /Resolve?tmdbId=X&tier=permanent
    1. Check AIOStreams for best source
    2. Apply HA/failover logic (existing hierarchy)
    3. Return HLS/MP4 URL
    4. Log resolution for watch tracking
```

**GHOST (IID library)**
```
Emby reads .strm
  -> GET /Resolve?tmdbId=X&tier=iid
    1. Check AIOStreams for best source
    2. If found:
       a. Return stream URL immediately (don't make user wait)
       b. ASYNC: trigger promotion to RESIDENT
    3. If not found:
       a. Return 503 with DON'T PANIC payload
       b. Log miss for future IID population scoring
```

**Opinion:** The IID resolver returns the stream first, promotes second. The user never waits for the promotion. From their perspective the stream just plays. The promotion is invisible plumbing.

---

## The Pruning Rules

RESIDENT items are prunable only when **all three** are true simultaneously:

```
CONDITION                                    WHY
---------------------------------------------------------
AIO feed no longer carries this TMDB ID  |  Source dried up
watch_progress < 10% OR never watched   |  No real engagement
favorited = false                        |  No expressed intent
```

If any one is false, the item stays. All three required to prune.

CITIZEN: **Never pruned by the system.** Only explicit user or admin action.

GHOST pruning triggers when:
- In IID longer than configured TTL (default 30 days)
- AND never interacted with (no play attempts, no detail views)
- AND no longer in any active feed

Ghosts that fell off feeds but were recently viewed: extend TTL by 14 days. Interest was shown.

---

## The DB Schema

```sql
CREATE TABLE media_items (
  id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tmdb_id                 INTEGER NOT NULL,
  media_type              TEXT NOT NULL CHECK (media_type IN ('movie','series')),

  -- Citizenship
  citizenship             TEXT NOT NULL DEFAULT 'ghost'
                          CHECK (citizenship IN ('ghost','resident','citizen','sovereign')),
  pinned                  BOOLEAN NOT NULL DEFAULT FALSE,
  pinned_at               TIMESTAMPTZ,
  pinned_by               UUID,

  -- Promotion tracking
  promoted_at             TIMESTAMPTZ,
  promoted_by             UUID,
  promotion_trigger       TEXT CHECK (promotion_trigger IN
                            ('play','watch_50','favorite','explicit')),

  -- File paths (only one active at a time)
  active_strm_path        TEXT,
  iid_strm_path           TEXT,
  permanent_strm_path     TEXT,

  -- Sovereign detection
  superseded_by_sovereign BOOLEAN NOT NULL DEFAULT FALSE,
  sovereign_detected_at   TIMESTAMPTZ,

  -- Feed tracking
  source_feeds            TEXT[] NOT NULL DEFAULT '{}',
  last_feed_seen_at       TIMESTAMPTZ,

  -- Watch state (for pruning decisions)
  last_watched_at         TIMESTAMPTZ,
  watch_progress_pct      SMALLINT DEFAULT 0,
  favorited               BOOLEAN NOT NULL DEFAULT FALSE,

  -- Emby linkage
  emby_item_id            TEXT,

  -- Audit
  created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at              TIMESTAMPTZ NOT NULL DEFAULT now(),

  UNIQUE (tmdb_id, media_type)
);

CREATE INDEX idx_citizenship         ON media_items (citizenship);
CREATE INDEX idx_tmdb_lookup         ON media_items (tmdb_id, media_type);
CREATE INDEX idx_sovereign           ON media_items (citizenship)
                                     WHERE citizenship = 'sovereign';
CREATE INDEX idx_prunable_residents  ON media_items (last_feed_seen_at, watch_progress_pct, favorited)
                                     WHERE citizenship = 'resident';
CREATE INDEX idx_ghost_ttl           ON media_items (created_at, last_feed_seen_at)
                                     WHERE citizenship = 'ghost';
```

---

## User-Facing Controls

**For RESIDENT items:**
```
[Pin to My Library]     -> promotes to CITIZEN
[Remove]                -> demotes to GHOST (or deletes if off all feeds)
```

**For CITIZEN items:**
```
[Pinned (on)]           -> toggle off = demote to RESIDENT
[Remove from Library]   -> demotes to RESIDENT first, confirmation required
[Permanently Remove]    -> demotes to GHOST, second confirmation required
```

**For GHOST items (found via search):**
```
[Add to My Library]     -> promotes directly to CITIZEN (explicit intent)
```

**Opinion:** Never allow one-tap delete from CITIZEN to gone. Two steps minimum. Apple makes you confirm twice when removing purchased content because they know you might regret it. You should too.

---

## Admin Settings Menu

```
Improbability Drive
  |- IID Population:      [Enabled / Disabled]
  |- IID Size Limit:      [500 / 1,000 / 5,000 / 10,000 / Unlimited]
  |- IID TTL:             [7 / 14 / 30 / 60 days]
  |- Ghost Prune:         [Daily / Weekly]
  |- [Run Sovereign Reconciliation Now]
  |- [Flush IID Cache]

Citizen Management
  |- [View all Citizens]    sortable list
  |- [View all Residents]   sortable list
  |- [Bulk Unpin]           by feed / by age / by unwatched
  |- [Export Citizenship Report]

Per-item admin override:
  |- Force to Citizen
  |- Force to Resident
  |- Force to Ghost
  |- Mark Sovereign (manual override)
  |- [View audit trail]
```

---

## The Complete Flow, Narrated for Grandma

```
1. She opens Emby on Roku.
   Sees "Trending Now" row — populated from Ghosts via Collections.
   Clicks Dune Part Two.

2. Emby loads the detail page from the IID library.
   Looks completely native.

3. She hits Play.

4. IID resolver fires.
   Checks AIOStreams, finds a stream, returns it in under 200ms.

5. Simultaneously, in the background:
   Ghost promoted to Resident.
   IID .strm renamed to permanent path.
   DB updated.
   Targeted library scan triggered.

6. She watches 80% of it.
   Watch threshold crossed.
   Resident promoted to Citizen.
   pinned = true.

7. Next time she opens Emby:
   Dune Part Two is in Continue Watching.
   It is in her library.
   It is searchable from any client.
   It will never disappear from a sync.

8. She never knew any of this happened.
```

That's the product.

---

## What to Build First

```
1. DB schema + ItemStateService
   The state machine as a service. Everything else calls this.

2. SOVEREIGN reconciliation job
   Run once at install. Protects Curated library from day one.

3. IID resolver endpoint
   Play must work before promotion matters.

4. Promotion pipeline
   Async, triggered by play event.

5. IID population scheduler
   Ghosts can't be promoted if they don't exist.

6. Pruning scheduler
   Keeps the IID lean.

7. User and Admin UI controls
   Last, because the plumbing has to work first.
```
