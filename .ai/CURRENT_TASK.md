---
status: complete
task: Sprint 144 — RefreshTask Notify + Verify + auto-pin on playback
phase: Complete
last_updated: 2026-04-10

## Sprint 144 Summary

Sprint 144 completed successfully with the following implementation:

### Phase 1: Database Methods ✅
- Added `GetCatalogItemsByStateAsync(ItemState state, int limit)` to DatabaseManager
- Added `GetCatalogItemsWithExpiringTokensAsync(int limit)` to DatabaseManager
- Added `GetCatalogItemByStrmPathAsync(string strmPath)` to DatabaseManager

### Phase 2: RefreshTask Notify Step ✅
- Added ILibraryManager dependency injection to RefreshTask
- Implemented `NotifyStepAsync()` with 42-item bound (NotifyLimit constant)
- Uses QueueLibraryScan() as fallback for surgical notification
- Transitions Written items to Notified state

### Phase 3: RefreshTask Verify Step ✅
- Implemented `VerifyStepAsync()` with 42-item bound
- Checks .strm files exist on disk for Notified items
- Transitions to Ready state when .strm files confirmed
- Implemented `RenewTokensAsync()` for items expiring within 90 days
- Shares 42-item budget between Verify and token renewal

### Phase 4: Stalled-Item Promotion ✅
- Implemented `PromoteStalledItemsAsync()`
- Promotes Notified items >24h to NeedsEnrich
- Sets nfo_status = 'NeedsEnrich'

### Phase 5: Auto-pin on Playback ✅
- Added UserPinRepository to Plugin.Instance
- Updated EmbyEventHandler to auto-pin on playback
- Inserts user_item_pins row with pin_source='playback'
- Works for all EmbyStreams .strm files

### Files Modified
1. `Data/DatabaseManager.cs` - Added query methods for catalog items by state and strm path
2. `Tasks/RefreshTask.cs` - Added Notify, Verify, token renewal, and stalled-item promotion steps
3. `Services/EmbyEventHandler.cs` - Added auto-pin on playback logic
4. `Plugin.cs` - Added UserPinRepository initialization
5. `plugin.json` - Updated version to match assembly (0.51.0.0)

### Build Status
✅ Build succeeded with 0 errors, 1 warning (MSB3052 is harmless)

### Notes
- InternalItemsQuery was not available in the Emby SDK being used, so simplified to file existence check
- Surgical notification pattern was mentioned in spec but QueueLibraryScan is used as fallback
- Auto-pin works for all .strm files regardless of user library path configuration
