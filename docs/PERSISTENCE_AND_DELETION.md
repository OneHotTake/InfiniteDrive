# Persistence, Saved Items, & Deletion

## 1. Saved Items (Personal Library)
"Saved Items" are the bridge between a raw manifest and the user's Emby library.
- **Intent:** A user "Saving" an item triggers the **Optimistic Sync**. 
- **Persistence:** Once saved, the `CatalogItem` is marked as `IsPersisted = true` in the DB. This prevents the cleanup sweep from removing it even if it's missing from a manifest update.

## 2. The Deletion Matrix
Deletion is the most dangerous operation in the system. It follows a strict hierarchy:

| Deletion Type | Trigger | Condition |
| :--- | :--- | :--- |
| **User Deletion** | UI Request | Immediate. Wipes DB record, .strm, and NFO. |
| **Pessimistic Deletion**| Health Check | Only if 404 on BOTH providers AND not throttled. |
| **Orphan Cleanup** | Marvin Task | Removes files on disk that have no DB entry. |
| **Version Rotation** | StrmWriter | Deletes old .strm versions when a higher quality is verified. |

## 3. Cleanup Guardrails
To prevent catastrophic library wipes (e.g., if a provider API goes down entirely):
- **The 50% Rule:** If a single task cycle attempts to delete >50% of the library, the task MUST abort and trigger a `NeedsReview` status in the Dashboard.
