# Stream Resolution & Failover Protocol

Stream resolution is the most volatile part of the system. This document defines the contract between the `ResolverService` (the API) and the `StreamResolutionHelper` (the Engine).

## 1. The Resolution Contract
We never return `null` or a raw URL. All resolution attempts must return a `ResolutionResult` object. This prevents "silent failures" where the system might assume content is missing just because a network request timed out.

### ResolutionResult Statuses
| Status | Meaning | Action |
| :--- | :--- | :--- |
| **Success** | URL found and validated. | Play immediately / Update cache. |
| **Throttled** | Provider returned 429 (Rate Limit). | **SHUTDOWN** attempts for this item. Do NOT delete. Retry in next cycle. |
| **ProviderDown** | Provider 5xx or Connection Timeout. | Trigger **Failover** to the Secondary Manifest. |
| **ContentMissing**| Provider returned 404 on this manifest. | Check other manifest. If 404 on BOTH, mark for Pessimistic Deletion. |

## 2. The Failover Logic (Multi-Manifest)
InfiniteDrive is designed to be manifest-agnostic. The logic follows a "Cascading Trust" model:

1.  **Primary Manifest:** Attempt resolution.
2.  **Circuit Breaker Check:** If the Primary AIOStreams host is down, `ResolverHealthTracker` trips.
3.  **The Secondary Pivot:** If Primary is `ProviderDown` or `ContentMissing`, the system MUST attempt the same resolution against the Secondary Manifest.
4.  **Terminal Failure:** An item is only considered "Dead" if the result from the final configured manifest is `ContentMissing`.

## 3. Throttling & "The Days-Long Hydration"
Because we throttle heavily to stay within AIOStreams' limits:
- The **Optimistic Phase** assumes every item is a `Success`.
- The **Pessimistic Hydration** phase uses a "Back-off" strategy.
- If a `Throttled` status is received, the `HydrationManager` must cease requests for that specific provider for the duration of the `RetryAfter` window.

## 4. Playback Strategy
During a live playback request (`/resolve`):
- We serve the **Best Quality** currently known.
- If the cached URL is expired, we trigger a "Fast-Path" resolution.
- If the Fast-Path returns `Throttled`, we attempt to serve a lower-quality cached version or a secondary manifest link before returning a 429 to the client.

## 5. Security (HMAC)
All resolved URLs passed to the `.strm` files must be signed via `PlaybackTokenService`.
- **Rule:** No URL leaves the system without a signature.
- **Rule:** Signatures must have an expiry matching the provider's token TTL (if known).

## 6. Language-Aware Resolution

When multiple cached candidates exist for an item (from `stream_candidates`), the `ResolverService` applies a language fallback chain:

1. Parse `X-Emby-Token` from request headers via `IAuthorizationContext`
2. Read the user's `PreferredMetadataLanguage`
3. If empty, fall back to `Config.MetadataLanguage` (global plugin setting)
4. If candidates have different `Languages` fields, prefer those matching the resolved language
5. Falls through to rank-order selection if no language match found

### MediaStreams (Version Picker)

`AioMediaSourceProvider` populates `MediaSourceInfo.MediaStreams` for each stream:

**Audio streams** — built from `AioStreamsStream.ParsedFile`:
- One `MediaStream` per language in `ParsedFile.Languages`
- Title format: `"{language} - {channels} {audioTags}"` (e.g. "Japanese - 5.1 Atmos")
- First language marked `IsDefault = true`

**Subtitle streams** — built from `AioStreamsStream.Subtitles`:
- One `MediaStream` per subtitle entry
- `IsExternal = true`, `DeliveryUrl` and `Path` set to subtitle URL
- Language from `subtitle.Lang`

**Language sorting** — sources are sorted by a fallback chain: `PluginConfiguration.MetadataLanguage` → library's `PreferredMetadataLanguage` (looked up via `ILibraryManager.GetVirtualFolders()`) → no sort. Matching audio streams are marked `IsDefault = true`.
