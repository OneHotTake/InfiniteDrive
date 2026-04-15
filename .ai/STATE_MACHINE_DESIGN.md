# State Machine Consolidation — Design Document

**Sprint 304-06 (Design Only)**
**Date:** 2026-04-15

---

## Overview

The codebase currently has two competing state enums that create confusion:
- `ItemState` (in `Models/ItemState.cs`)
- `ItemStatus` (used in database schema and services)

This document proposes a unified state machine that consolidates both models and covers all use cases.

---

## Current State Models

### ItemStatus (Database/Schema.cs)

Used in `media_items` table with CHECK constraint:

```sql
status TEXT NOT NULL CHECK (status IN ('known','resolved','hydrated','created','indexed','active','failed','deleted'))
```

Meanings:
- `known` — Item discovered in catalog, metadata fetched
- `resolved` — Streams resolved from AIOStreams
- `hydrated` — .strm/.nfo files written
- `created` — Emby library entry created
- `indexed` — Emby has indexed the item
- `active` — Item is being actively used
- `failed` — Item processing failed
- `deleted` — Item removed from library

### ItemState (Models/ItemState.cs)

```csharp
public enum ItemState
{
    Unknown,
    Pending,
    Resolved,
    Hydrated,
    Indexed,
    Failed,
}
```

---

## Identified Conflicts

1. **Status Mismatch:** `ItemStatus.active` has no equivalent in `ItemState`
2. **Status Overlap:** `ItemStatus.known` ≈ `ItemState.Pending`
3. **Missing States:** Some database statuses lack explicit ItemState mapping
4. **Transition Ambiguity:** Unclear how states transition between the two models

---

## Proposed Unified State Machine

### State Values

```csharp
public enum UnifiedItemState
{
    // Initial states
    Unknown = 0,

    // Discovery phase
    Discovered = 100,  // Found in catalog/manifest
    Known = 101,       // Metadata fetched from Cinemeta/AIOMetadata

    // Resolution phase
    Resolving = 200,    // In progress: querying AIOStreams
    Resolved = 201,      // Streams successfully retrieved

    // Materialization phase
    Hydrating = 300,    // Writing .strm/.nfo files
    Hydrated = 301,      // Files written successfully

    // Library integration phase
    Creating = 400,       // Creating Emby item
    Created = 401,        // Emby item created

    // Indexing phase
    Indexing = 500,       // Emby scanner running
    Indexed = 501,         // Emby has indexed

    // Final states
    Active = 600,          // Currently in user's library/playback
    Failed = 900,          // Processing failed, unrecoverable
    Deleted = 999,         // Removed from library
}
```

### State Transition Diagram

```
Unknown → Discovered → Known → Resolving → Resolved → Hydrating
                                                                   ↓
Hydrated → Creating → Created → Indexing → Indexed → Active
                                                              ↓
                                                   (any failure state) → Failed
                                                   (user/admin action) → Deleted
```

### Transitions Table

| From | To | Trigger | Service |
|------|-----|----------|----------|
| Unknown | Discovered | Catalog sync discovers item | CatalogSyncService |
| Discovered | Known | Metadata fetch succeeds | IdResolverService |
| Known | Resolving | AIOStreams query initiated | AioStreamsClient |
| Resolving | Resolved | Streams returned successfully | AioStreamsClient |
| Resolving | Failed | Timeout/no streams | StreamResolver |
| Resolved | Hydrating | .strm write initiated | StrmWriterService |
| Hydrating | Hydrated | Files written | StrmWriterService |
| Hydrated | Creating | Emby item creation | LibraryPostScanReadoptionService |
| Creating | Created | Item exists in Emby | Emby API |
| Created | Indexing | Library scan triggered | Emby scheduler |
| Indexing | Indexed | Scan complete | Emby |
| Indexed | Active | First access/user save | PlaybackService |
| (any) | Failed | Error at any stage | Various |
| (any) | Deleted | User/admin deletion | AdminService/User |

---

## Migration Path

### Phase 1: Add Unified Enum
1. Create `Models/UnifiedItemState.cs` with new enum
2. Keep old enums deprecated with `[Obsolete("Use UnifiedItemState")]`

### Phase 2: Database Schema Change
1. Add migration script:
```sql
ALTER TABLE media_items ADD COLUMN unified_state INTEGER DEFAULT -1;
UPDATE media_items SET unified_state =
    CASE status
        WHEN 'known' THEN 101
        WHEN 'resolved' THEN 201
        WHEN 'hydrated' THEN 301
        WHEN 'created' THEN 401
        WHEN 'indexed' THEN 501
        WHEN 'active' THEN 600
        WHEN 'failed' THEN 900
        WHEN 'deleted' THEN 999
        ELSE 0
    END;
```

2. Update constraints to use new enum

### Phase 3: Service Updates
1. Replace `ItemState` references with `UnifiedItemState`
2. Update database queries to read/write `unified_state` column
3. Update transition logic in:
   - ItemPipelineService
   - CatalogSyncTask
   - StrmWriterService
   - LibraryPostScanReadoptionService

### Phase 4: Cleanup
1. Drop `status` column from `media_items` (after验证期)
2. Remove `Models/ItemState.cs`
3. Remove `ItemStatus` enum references

---

## Affected Files

**Core Models:**
- `Models/UnifiedItemState.cs` (NEW)
- `Models/ItemState.cs` (DELETE after Phase 4)

**Database:**
- `Data/Schema.cs` — Update table definition
- `Data/DatabaseManager.cs` — Add migration V31

**Services:**
- `Services/ItemPipelineService.cs` — Major refactoring
- `Services/CatalogSyncTask.cs` — Update state transitions
- `Services/StrmWriterService.cs` — Update hydration logic
- `Services/LibraryPostScanReadoptionService.cs` — Update creation logic

---

## Estimated Effort

| Phase | Estimated Time | Risk |
|--------|----------------|-------|
| Phase 1 (Enum) | 1 hour | LOW |
| Phase 2 (Schema) | 2 hours | MEDIUM (requires migration testing) |
| Phase 3 (Services) | 4 hours | HIGH (touches core pipeline) |
| Phase 4 (Cleanup) | 1 hour | LOW |
| **Total** | **8 hours (~1 day)** | **MEDIUM** |

---

## Open Questions

1. Should `Active` state be persisted or runtime-only?
2. Should we preserve audit trail of state transitions in `item_pipeline_log`?
3. Is backward compatibility needed for existing external tools reading the database?

---

**Status:** Design complete, awaiting implementation sprint approval.
