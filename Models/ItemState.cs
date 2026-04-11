using System;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// Item states for the Marvin reconciliation engine (Sprint 66).
    /// Every catalog item has exactly one state at any time.
    ///
    /// State transitions:
    /// CATALOGUED → PRESENT   (Marvin Phase 2: writes .strm to disk)
    /// PRESENT    → RESOLVED  (Link Resolver: caches valid stream URL)
    /// RESOLVED   → PRESENT   (Marvin Phase 5: dead URL detected, re-queue)
    /// RESOLVED   → RETIRED   (Marvin Phase 3: real file found in Emby library)
    /// PINNED     → RESOLVED  (Discover: Add to Library → immediate resolve)
    /// PINNED     → RETIRED   (Marvin Phase 3: real file found, PIN cleared)
    /// ORPHANED   → [deleted] (Marvin Phase 1: item removed from catalog, no PIN)
    /// </summary>
    public enum ItemState
    {
        /// <summary>
        /// Item exists in DB from sync, no .strm on disk yet.
        /// Transitions to: PRESENT (Marvin Phase 2)
        /// </summary>
        Catalogued = 0,

        /// <summary>
        /// .strm file exists on disk, URL not yet resolved.
        /// Transitions to: RESOLVED (Link Resolver)
        /// </summary>
        Present = 1,

        /// <summary>
        /// .strm on disk + valid cached stream URL.
        /// Transitions to: PRESENT (stale URL), RETIRED (real file found)
        /// </summary>
        Resolved = 2,

        /// <summary>
        /// Real file detected in Emby library; .strm deleted.
        /// Terminal state — no further transitions.
        /// </summary>
        Retired = 3,

        /// <summary>
        /// .strm on disk but item no longer in catalog (and not PINNED).
        /// Will be purged in Marvin Phase 2.
        /// </summary>
        Orphaned = 4,

        /// <summary>
        /// User explicitly added via Discover "Add to Library".
        /// Protected from catalog removal. Can transition to RETIRED if real file appears.
        /// </summary>
        Pinned = 5,

        // ── Refresh lifecycle states (Sprint 142) ─────────────────────────

        /// <summary>New/changed item awaiting .strm write</summary>
        Queued = 6,

        /// <summary>.strm on disk, awaiting Emby notification</summary>
        Written = 7,

        /// <summary>Emby notified, awaiting verification</summary>
        Notified = 8,

        /// <summary>Fully verified, item is live</summary>
        Ready = 9,

        /// <summary>NFO enrichment needed</summary>
        NeedsEnrich = 10,

        /// <summary>Enrichment failed after max retries</summary>
        Blocked = 11
    }
}
