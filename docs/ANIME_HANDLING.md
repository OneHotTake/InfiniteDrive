# Anime Handling & ID Resolution

## 1. The ID Impedance Mismatch
Anime items often lack 1:1 parity between Western providers (TMDB/TVDB) and Anime trackers (AniDB/MAL/Kitsu). 
- **The Golden Rule:** We prioritize **Kitsu** for internal tracking but must maintain a cross-reference table for Emby compatibility.
- **Correction of Drift:** Never store a Kitsu ID in an `AniDbId` field. Each provider must have a dedicated, nullable column in the `CatalogItem` table.

## 2. ID Resolution Flow (The "Translation Layer")
When an anime item is discovered:
1. **Primary ID:** The manifest usually provides a Kitsu or MAL ID.
2. **Translation:** `MetadataService` attempts to resolve the missing Western IDs (TMDB/TVDB) via a lookup.
3. **Fallthrough:** If no Western ID is found, the item is tagged with `ExternalCategory = "Anime"`. This forces the `NamingPolicyService` to use the `[kitsu-ID]` tag instead of `[tmdbid-ID]`.

## 3. Absolute Numbering vs. Seasons
Anime often uses absolute episode numbering (e.g., Episode 500) while Emby expects Seasons (e.g., S21E12).
- **Expansion Logic:** `SeriesPreExpansionService` must use the `AnimeMappingHelper` to convert absolute numbers into Season/Episode structures during the **Optimistic Phase**.
- **NFO Handling:** `NfoWriterService` must write the `<displayepisode>` and `<displayseason>` tags to ensure Emby's UI remains navigable for users who prefer absolute numbering.

## 4. Special Folder Handling
Anime folders often require different sanitization (handling of brackets for fansub groups, Romanized vs. Kanji titles).
- **Naming Priority:** `NamingPolicyService` uses the `RomajiTitle` if available, falling back to the `EnglishTitle`.
- **Sanitization:** Fansub tags in filenames (e.g., `[SubsPlease]`) are preserved in the filename for versioning but stripped from the **Folder Name** to maintain library cleanliness.

## 5. Metadata Fallback (The Kitsu Bridge)
If AIOStreams returns metadata that lacks Season/Episode info for an Anime:
- The system must query the `KitsuProxy` to "hydrate" the episode list.
- This is part of the **Pessimistic Hydration** cycle and must be throttled to 1 request per 2 seconds to respect Kitsu's API limits.
