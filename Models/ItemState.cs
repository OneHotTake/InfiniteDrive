using System;

namespace EmbyStreams.Models
{
    /// <summary>
    /// Item states for the Doctor reconciliation engine (Sprint 66).
    /// Every catalog item has exactly one state at any time.
    ///
    /// State transitions:
    /// CATALOGUED → PRESENT   (Doctor Phase 2: writes .strm to disk)
    /// PRESENT    → RESOLVED  (Link Resolver: caches valid stream URL)
    /// RESOLVED   → PRESENT   (Doctor Phase 5: dead URL detected, re-queue)
    /// RESOLVED   → RETIRED   (Doctor Phase 3: real file found in Emby library)
    /// PINNED     → RESOLVED  (Discover: Add to Library → immediate resolve)
    /// PINNED     → RETIRED   (Doctor Phase 3: real file found, PIN cleared)
    /// ORPHANED   → [deleted] (Doctor Phase 1: item removed from catalog, no PIN)
    /// </summary>
    public enum ItemState
    {
        /// <summary>
        /// Item exists in DB from sync, no .strm on disk yet.
        /// Transitions to: PRESENT (Doctor Phase 2)
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
        /// Will be purged in Doctor Phase 2.
        /// </summary>
        Orphaned = 4,

        /// <summary>
        /// User explicitly added via Discover "Add to Library".
        /// Protected from catalog removal. Can transition to RETIRED if real file appears.
        /// </summary>
        Pinned = 5
    }
}
