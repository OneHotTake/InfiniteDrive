InfiniteDrive Settings Redesign — Final Spec v2.3

Version: 2.3
Goal: Flat, calm, modern Emby-native settings that feel first-party. No wizard, no pulsing buttons, no over-animation. Preserve ALL existing backend logic. Only the UI is changing (except Sprint 214 backend prerequisites).

IMPORTANT — Scope Boundary
configurationpage.html and configurationpage.js are the primary files being modified. Sprint 214 makes minimal, targeted backend changes that unblock the UI. All other C# backend logic is untouched.

Core Rules (strict)

Use Emby native classes first (card, inputContainer, emby-input, emby-button, emby-checkbox, tables, etc.).
Add only minimal <style> for increased padding, vertical rhythm, and soft card rounding. No animations, no pulsing, no "cute" effects whatsoever.
One floating "Save All Settings" button (bottom-right, standard Emby green) on config-heavy tabs only (Libraries & Servers, Security, Parental Controls).
Dirty state: change button text to "Save Settings •" (dot suffix only). No color change, no glow.
After save: show "✓ Saved" as plain text next to the button for 3 seconds, then revert. No toast.
Per-tab "Refresh" button at top-right on tabs with live data (Providers, Sources, System Health, Repair).
Small status pills in top-right of the tab bar showing AIO + Backup status (✅ / 🔴).
All timestamps human-relative ("3 minutes ago", "just now") via the existing fmtRelative() function. No raw seconds, no negative values, ever.
Friendly source names everywhere. Raw IDs only in row tooltips.
Anime library is always created — no toggle. Clear note in the UI.
Tab Order (final)

Providers | Libraries & Servers | Sources | Security | Parental Controls | System Health | Repair

Tab 1: Providers (default tab)

Getting Started Card

Computed entirely on the frontend from existing config values. No new backend fields.

Completion logic:

Step 1 complete: config.AioStreamsUrl is non-empty AND last connection test was successful
Step 2 complete: config.BaseSyncPath is non-empty AND config.EmbyBaseUrl is non-empty
Step 3 complete: at least one source has enabled = true (check AioStreamsCatalogIds is non-empty, or fallback to checking EnableAioStreamsCatalog = true)
Incomplete state:

unknown
Copy
Getting Started

1. Connect AIOStreams Manifest          ✅ Done
2. Configure Libraries & Servers        → Go to Libraries tab
3. Choose Sources                       → Go to Sources tab

Once complete, your library will begin populating automatically.
[ View Setup Guide ]
Complete state (all three steps done):

unknown
Copy
✅ Setup complete — InfiniteDrive is configured and syncing.
                                              [ Review Guide ]
Provider Cards

AIOStreams Primary

The URL field displays the reconstructed manifest URL built from AioStreamsUrl + AioStreamsUuid + AioStreamsToken on load (these are the persisted fields; the manifest URL is a convenience format). When the user pastes a new URL, the existing parseManifestUrl() function extracts the components for storage.

unknown
Copy
AIOStreams                                                        REQUIRED

Your AIOStreams manifest URL. This is the only required field.

Manifest URL
[ https://yourdomain.com/stremio/uuid/token/manifest.json ]

Don't have one?  [ Create one with Stremio Tools → ]

[ Refresh ]   [ Edit ]

Last refreshed: 3 hours ago  •  47 sources
[ Edit ] button behavior: Opens data.ManifestConfigureUrl from GET /InfiniteDrive/Status. Before opening the tab, show a one-line interstitial popover:

unknown
Copy
Opening AIOStreams configuration on:
https://your-aiostreams.host

Sign in as: [current Emby username from ApiClient.getCurrentUser().Name]

[ Open AIOStreams Config → ]
This reminds the user which account to use without requiring AIOStreams to support prefill.

Refresh / Connect & Sync button: Tests the manifest URL using the existing test endpoint, saves on success, triggers source sync if catalog diff detected. Shows inline progress:

unknown
Copy
Refreshing…  Fetching manifest…  47 sources found.  ✅ Done.
On failure:

unknown
Copy
❌ Could not reach manifest. Check the URL and try again.
   (Details: System Health → Advanced Debug Tools)
AIOStreams Backup

The backup URL field, when populated, implicitly enables backup (EnableBackupAioStreams = true). When cleared, backup is disabled. No separate toggle checkbox. This is a backend behavior change from Sprint 214.

unknown
Copy
AIOStreams Backup                                      HIGHLY RECOMMENDED

Instances go down. A backup keeps you streaming without interruption.

Backup Manifest URL
[ https://backup-domain.com/.../manifest.json ]
Leave blank to disable backup.

[ Refresh ]   [ Edit ]

Last refreshed: 3 hours ago
URL field always visible. Edit button shows same username interstitial as primary, pointing to backup instance's ManifestConfigureUrl.

AIOMetadata

unknown
Copy
AIOMetadata                                                       OPTIONAL

[ ] Enable AIOMetadata

Supplemental fallback for titles Emby cannot identify.
Most setups don't need this.

AIOMetadata Base URL
[ https://yourdomain.com/aio-metadata ]

[ Refresh ]   [ Edit ]
URL field and buttons hidden when toggle is off. Edit button opens {AioMetadataBaseUrl}/configure with the same username interstitial.

Cinemeta

unknown
Copy
Cinemeta                                                          OPTIONAL

[ ] Use Cinemeta as a metadata fallback

Stremio's public catalog. Last-resort fallback for titles that neither 
Emby nor AIOMetadata can identify. Great for anime and international content.
Toggle only. No URL field. No Edit button.

System RSS Feeds

unknown
Copy
System RSS Feeds                                                  OPTIONAL

RSS feeds added here are available to every user on this server.

⚠️  Content from system RSS feeds is visible to all users regardless of 
    parental controls. Only add feeds appropriate for your entire audience.

Feed URLs (one per line)
[ https://example.com/feed.rss                                          ]

Display Name (optional)
[ e.g. "Staff Picks" ]

[ Save Feeds ]   ← inline save, binds to config.SystemRssFeedUrls
Tab 2: Libraries & Servers

Pure config tab. Floating Save button. Field bindings are read-only from Emby on first run — see Metadata Preferences section.

Library Locations

unknown
Copy
Library Locations

Base sync path
[ /media/infinitedrive ]
Subfolders created automatically:  movies/  •  shows/  •  anime/

Movies library name
[ Streamed Movies ]

Series library name
[ Streamed Series ]

Anime library name
[ Streamed Anime ]
Anime is always created as a separate library. Anime titles use different 
metadata conventions that require their own library.
Emby Server

Preserve the localhost detection banner exactly as it exists today.

unknown
Copy
⚠️  Localhost URL detected
    Remote clients won't be able to play streams on localhost.
    Updated to: http://192.168.1.21:8096

Emby Server URL
[ http://192.168.1.21:8096 ]
Inferred from your browser. Change only if remote clients access Emby 
via a domain name or reverse proxy.
Metadata Preferences

On first run or when fields are empty: Auto-populated silently from ApiClient.getServerConfiguration() (PreferredMetadataLanguage, MetadataCountryCode). The dropdowns are hidden by default, replaced by a summary line:

unknown
Copy
Metadata Preferences

Synced from your Emby server settings.
Language: English  •  Country: United States  •  Artwork: English

[ Override → ]
Clicking Override expands the three dropdowns (existing cfg-meta-lang-dropdown, cfg-meta-country-dropdown, cfg-meta-img-lang-dropdown). Once overridden, the summary line shows "Custom" and the dropdowns remain visible.

unknown
Copy
[ ] Write .nfo metadata files
    Creates .nfo files alongside .strm files for backup and recovery.
Preserved Field Bindings (DO NOT RENAME)

loadConfig() and saveConfig() must read/write these exact property names:

Field	Config property	Type
Base sync path	config.BaseSyncPath	string
Movies library name	config.LibraryNameMovies	string
Series library name	config.LibraryNameSeries	string
Anime library name	config.LibraryNameAnime	string
Emby Server URL	config.EmbyBaseUrl	string
Metadata language	config.MetadataLanguage	string (ISO 639-1)
Country	config.MetadataCertificationCountry	string (ISO 3166-1)
Artwork language	config.MetadataImageLanguage	string (ISO 639-1)
Write .nfo files	config.WriteNfoFiles	bool
Note: SignatureValidityDays does not exist — do not reference it anywhere.

Tab 3: Sources

Preserve the existing catalog table layout. Auto-save on checkbox and limit interaction. Remove ▲ ▼ order controls entirely — sources are unordered.

Top-right buttons: [ Refresh Sources ] [ Sync All Now ]

unknown
Copy
These are the catalogs from your AIOStreams manifest. Enable the ones you 
want synced. Changes take effect immediately.

☑  SOURCE                          TYPE     PROGRESS    LIMIT
────────────────────────────────────────────────────────────
☑  Popular — Movies                movie    ████░░ 20   100
☑  Popular — Series                series   ████░░ 20   100
☑  Top 10 — Movies                 movie    ██████ 48   100
   Disney+ — Movies                movie    ────── 0    100
☑  Top Anime (AniList)             anime    █████░ 38   100
Source display name logic:

Use catalog.name from the Stremio manifest JSON. Format as [catalog.name] — [catalog.type capitalized]
If two catalogs share name + type, append provider domain: "Search — Movies (aiostreams)"
If catalog.name is missing, clean the raw ID: replace : and . with spaces, title-case. e.g. "aio:movie:top" → "Aio Movie Top"
Raw ID available in row title tooltip on hover.
Serialization: Enabled sources serialize to config.AioStreamsCatalogIds (comma-separated set of enabled IDs — order does not matter). Per-source limits serialize to config.CatalogItemLimitsJson. Do not change these serialization formats.

Last full sync shown below table: "Last full sync: 4 hours ago"
[ Select All ] [ Select None ]

Add Custom Source

unknown
Copy
+ Add Custom Source

Friendly Name
[ e.g. "My Private RSS Feed" ]

Source URL
[ https://... ]

[ Validate Source ]   [ Add to Library ]  ← disabled until validation passes
Tab 4: Security

Pure config tab. Floating Save is not needed here — the single action is the Rotate button which is self-contained.

Playback Security

One card. No complexity exposed to the user.

unknown
Copy
Playback Security

InfiniteDrive signs all stream URLs using a unique secret key.
This happens automatically — you never need to manage it.

[ Rotate Secret ]

Rotating generates a new key, rebuilds all stream files using the new key,
then activates the new key. Your library never breaks — files are rebuilt 
before the old key is retired.

Last rotated: 47 days ago
[ Rotate Secret ] button behavior:

Show confirmation modal:
unknown
Copy
Title:   Rotate Signing Secret?
Body:    This rebuilds all .strm files with a new signing key.
         Your library remains fully playable throughout.
         This may take a few minutes.
Buttons: [ Cancel ]   [ Rotate ]
After confirmation, call POST /InfiniteDrive/Setup/RotateApiKey (existing endpoint, enhanced in Sprint 214 for two-phase safety).
Show inline progress in the card:
unknown
Copy
🔄  Rebuilding stream files…
████████████░░░░  (progress from GET /InfiniteDrive/Setup/RotationStatus)

✅  Done. All stream files updated.   Last rotated: just now
On failure: ❌ Rotation failed. Your existing stream files are unchanged. Check System Health for details.
Note: PluginSecretRotatedAt (unix timestamp) is added in Sprint 214 to PluginConfiguration.cs. The UI reads this to display "Last rotated: N days ago" via fmtRelative().

Storage Note

unknown
Copy
ℹ️  Storage Note
Config is stored in plaintext at config/plugins/InfiniteDrive.xml.
Restrict filesystem access to your Emby config directory to trusted users only.
Tab 5: Parental Controls

Pure config tab. Floating Save for TMDb key and Block Unrated toggle. Block and Remove actions are immediate — no Save needed.

Content Filtering

Preserve the existing info banner, TMDb key field, Behavior Matrix table, and Block Unrated checkbox exactly.

unknown
Copy
[info banner — preserve exactly]

TMDb API Key
[ Your TMDb API key ]   [ Test Key ]
Required for MPAA/TV ratings.  Get a free key at themoviedb.org → Settings → API

Behavior Matrix
[exact table from current screenshots — preserve completely]

[ ] Block Unrated for Restricted Users

Filtering Status
⚠️  No TMDb key configured — parental filtering is inactive
Blocked Content

Block search queries the local InfiniteDrive catalog only — items managed by this plugin. Physical media files added by the user cannot be blocked here. The search calls GET /InfiniteDrive/Admin/SearchItems?q=... (new endpoint, Sprint 214) which queries media_items filtered to InfiniteDrive-managed items.

Item identity is the InfiniteDrive internal id (UUID from media_items table), not an IMDb ID. Display shows the friendly title. External ID types (IMDb, TMDb, TVDb, Kitsu, MAL) are shown as supplementary metadata — not as keys. This correctly handles anime that has only a Kitsu ID.

unknown
Copy
Blocked Content

Search for a title in your InfiniteDrive library, then block it.

[ Search titles…                              ]   [ Search ]

Only content managed by InfiniteDrive appears here. Physical media 
files added separately are unaffected.

Search results (up to 5):
┌────────────────────────────────────────────────────────────────┐
│ 🎬  Dune: Part Two (2024)   movie   [ Block ]                  │
│ 🎬  Dune (2021)             movie   [ Block ]                  │
└────────────────────────────────────────────────────────────────┘

Clicking [ Block ] immediately blocks the item by internal ID.
Row updates to show ✅ Blocked. No Save required.

Currently Blocked
┌────────────────────────────────────────────────────────────────┐
│  🚫  Dune: Part Two (2024)     [Remove]                        │
│  🚫  Attack on Titan (2013)    [Remove]                        │
└────────────────────────────────────────────────────────────────┘
Display the friendly title. If title lookup fails, show "(title unavailable)".
External IDs (IMDb, TVDb, Kitsu, etc.) shown in row tooltip only.

[ Unblock Selected ]   [ Unblock All ]
Block endpoint: POST /InfiniteDrive/Admin/BlockItems — payload uses internal ItemIds (UUIDs), not ImdbIds. Sprint 214 updates this endpoint.

Tab 6: System Health

Read-only live view. Refresh button at top-right (30-second rate limit on manual refresh). No Save.

unknown
Copy
System Health                                            [ Refresh ]

Overall: Everything looks good ✅           Last checked: 2 minutes ago

┌─────────────────────────┐  ┌─────────────────────────┐
│ AIOStreams (Primary)     │  │ AIOStreams Backup        │
│ ✅ Online                │  │ ✅ Online                │
│ Last sync: 3 min ago    │  │ Ready                   │
│ 1,284 items             │  │                         │
└─────────────────────────┘  └─────────────────────────┘

┌─────────────────────────┐  ┌─────────────────────────┐
│ AIOMetadata             │  │ Cinemeta                │
│ ✅ Ready                 │  │ ✅ Fallback active       │
└─────────────────────────┘  └─────────────────────────┘

Notices
• 5 items have missing metadata     [ Fix with Marvin → ]
• All sources synchronized          Last full sync: 47 minutes ago
Provider card states: ✅ Online / ❌ Offline / ⚠️ Slow (response > 2 seconds)

Background Tasks

unknown
Copy
TASK              STATUS       SCHEDULE              ACTION
Source Sync       ✅ Idle      Daily at 03:00 UTC    [ Run Now ]
Link Resolver     ✅ Idle      Hourly                [ Run Now ]
Refresh Worker    ✅ Idle      Continuous            —
Advanced Debug Tools

Collapsed by default. All existing debug tools remain here exactly as-is.

unknown
Copy
▶ Advanced Debug Tools   (click to expand)
Tab 7: Repair

Action-only tab. No Save.

Marvin

unknown
Copy
Repair

Marvin is our helpful (if slightly depressed) robot assistant with a brain 
the size of a planet. He automatically finds and fixes broken or missing 
.strm files, metadata that never enriched, catalog sync problems, cache 
inconsistencies, and anything else that's quietly broken.

Don't Panic. Marvin has seen worse.

┌─────────────────────────────────────────────────────────────────────┐
│ 🟢  Refresh Worker      Idle                                        │
│ 🟢  Marvin              Idle                                        │
│                                                                     │
│ Needs Enrichment: 5     Blocked: 2     Missing Metadata: 3         │
└─────────────────────────────────────────────────────────────────────┘

[ Scan for Problems ]   ← read-only diagnostic, updates counts only

┌─────────────────────────────────────────────────────────────────────┐
│                    [ SUMMON MARVIN ]                                │
└─────────────────────────────────────────────────────────────────────┘
[ SUMMON MARVIN ] shows step-by-step progress, then on completion:

unknown
Copy
✅  Marvin   Done
Fixed 5 issues. Everything looks healthy.
Last run: just now
Content Management

unknown
Copy
Source Sync
Manually trigger a catalog fetch. Normally runs daily at 03:00 UTC.
[ Force Source Sync Now ]

Version Slots
Apply pending quality changes across all catalog items.
[ Run Pending Rehydration ]

Resolution Cache
[ Pre-Warm Cache Now ]
Danger Zone

Preserve the pink/red-bordered card design exactly as in current screenshots.

unknown
Copy
┌─ 🔴 DANGER ZONE ─────────────────────────────────────────────────────────┐
│  Recovery                                                                 │
│  Clears all InfiniteDrive settings. Library and .strm files preserved.   │
│  [ 🔄 Reset All Settings ]                                               │
│  ────────────────────────────────────────────────────────────────────    │
│  Purge Source Data                                                        │
│  Wipes all catalog items, .strm files, and sync state from disk.         │
│  [ Purge Source Data ]                                                    │
│  ────────────────────────────────────────────────────────────────────    │
│  Total Existence Failure                                          🔴      │
│  Wipes everything — database, .strm files, and all configuration.        │
│  Cannot be undone.                                                        │
│  [ ☢ Total Existence Failure ]                                           │
└───────────────────────────────────────────────────────────────────────────┘
Confirmation dialogs — escalating exactly as in v2.2.

What Is Deliberately Preserved

Element	Reason
Localhost URL detection banner	Prevents most common first-run failure
Parental Controls behavior matrix	Correct — keep exactly as-is
Danger Zone pink-bordered card	Escalating severity hierarchy is perfect
Sources table checkbox/limit	Already good UX
Marvin + DON'T PANIC tone	Keep
Advanced Debug Tools (collapsed)	Valuable for power users
Total Existence Failure name	Perfect escalating naming
Edit button → external provider URL	Existing behavior + username interstitial
fmtRelative() for all timestamps	Already in codebase
What Is Deliberately Removed / Changed

Element	Replaced by
3-screen wizard	Getting Started checklist card on Providers tab
Advanced tab	Contents absorbed into correct tabs
SignatureValidityDays field	Removed — field doesn't exist (Sprint 137)
Two separate "secrets" in Security tab	One card, one "Rotate Secret" button
Auto-rotation interval dropdown	DoctorTask handles silently (Sprint 141)
EnableBackupAioStreams checkbox	Presence of backup URL is the toggle
Metadata language/country/artwork dropdowns (default visible)	Auto-filled from Emby; hidden unless user overrides
Source ordering (▲ ▼)	Removed — was for defunct channel feature
Block by raw IMDb ID text input	Search local catalog → select → block by internal ID
ImdbIds in block endpoint payload	ItemIds (internal UUIDs)

