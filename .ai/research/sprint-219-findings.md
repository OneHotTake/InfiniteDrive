# Sprint 219 Findings — IChannel SDK Reality Check

## SDK Version
- Compile-time (libs/): MediaBrowser.Controller v4.10.0.8
- Compile-time (NuGet): MediaBrowser.Server.Core 4.9.1.90 (same types)
- Runtime DLL: ~/Projects/emby/emby-beta/opt/emby-server/system/ v4.10.0.8
- **All three sources produce identical type signatures.**

## Question 1: Does ISearchableChannel exist in the runtime?
**Answer:** YES — as a type definition. NO — it is never used.
**Evidence:**
- `ISearchableChannel` is defined in `MediaBrowser.Controller.Channels` as a public interface
- It has **zero methods, zero properties, zero inherited interfaces** — it is an empty marker interface
- `strings` grep on `Emby.Server.Implementations.dll` (the runtime ChannelManager) returns **ZERO results** for "ISearchableChannel"
- ChannelManager has **zero methods** containing "Search" in their name
- The interface is a dead artifact — the server never checks `channel is ISearchableChannel`

## Question 2: Does InternalChannelItemQuery have a SearchTerm/SearchQuery property?
**Answer:** NO
**Evidence:**
- `InternalChannelItemQuery` has exactly 6 properties: FolderId, UserId, StartIndex, Limit, SortBy, SortDescending
- `SearchTerm` exists on `InternalItemsQuery` (the general library query type) and `BaseItemsRequest`
- `SearchTerm` does NOT exist on any channel-specific type
- The channel system cannot receive or process search terms

## Question 3: Is there a second GetChannelItems overload on IChannel?
**Answer:** NO
**Evidence:**
- IChannel has a single `GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)` method
- No overload taking `ChannelItemSearchRequest`, `string searchTerm`, or any other search-capable parameter
- ChannelManager's private `GetChannelItems` bridge method takes `(IChannel, String externalFolderId, ...)` — folder routing only

## Question 4: What is the exact reflection property name for folder/item ID routing?
**Answer:** `FolderId` (type: String)
**Evidence:**
- `InternalChannelItemQuery.FolderId { get; set; }` — this is how Emby routes into channel folder hierarchies
- The ChannelManager bridge passes `externalFolderId` from `InternalItemsQuery` → `InternalChannelItemQuery.FolderId`

## Decision

**Search wiring verdict: IMPOSSIBLE — SDK wall confirmed.**

The channel system has no search path. `ISearchableChannel` is a dead marker interface that Emby's ChannelManager never reads. `InternalChannelItemQuery` lacks any search field. The only routing mechanism is `FolderId` for browsing folder hierarchies.

A browse-only IChannel (folders → items) IS possible. Search is NOT.

**Recommended next sprint:**
- Sprint 220: Build **browse-only** InfiniteDriveChannel (browse folders by catalog → items)
- Sprint 221: HomeSectionManager deeplink to Discover web UI for search (existing DiscoverService already handles search)
