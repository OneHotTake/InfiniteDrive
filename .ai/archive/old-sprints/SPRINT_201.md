# Sprint 201 — Wizard Backend Wiring: Make It Actually Work

**Version:** v2.0 (hardened) | **Status:** ✅ Implemented | **Risk:** MEDIUM | **Depends:** Sprint 200
**Owner:** Backend | **Target:** v0.41 | **PR:** TBD

---

## Overview

Three bugs mean completing the wizard today produces a broken installation:

1. **Libraries are never created.** `LibraryProvisioningService.RegisterEmbyLibrariesAsync()` is a stub — logs "Would register" and returns. No Emby libraries exist after setup.
2. **Anime files land in the wrong place.** `StrmWriterService.WriteAsync()` has no anime case — anime items fall through to `SyncPathShows` regardless of whether they are movies or series.
3. **Backup AIOStreams ignores the checkbox.** `AioStreamsClient` falls back to `SecondaryManifestUrl` unconditionally if the primary fails to parse, ignoring the user's intent.

### SDK facts confirmed before writing this sprint

- `ILibraryManager.AddVirtualFolder(string name, LibraryOptions options, bool refreshLibrary)` — confirmed in `MediaBrowser.Controller.dll` via SDK docs at `Emby.SDK-4.10.0.8-Beta/Documentation/reference/pluginapi/MediaBrowser.Controller.Library.ILibraryManager.html`
- `LibraryOptions.ContentType` (string) — sets collection type: `"movies"`, `"tvshows"`, `""` (empty = mixed/all)
- `LibraryOptions.PathInfos` (MediaPathInfo[]) — sets library paths
- `MediaPathInfo.Path` (string) — the directory path
- Namespace: `MediaBrowser.Model.Configuration` for both `LibraryOptions` and `MediaPathInfo`
- `EnsureLibrariesProvisionedAsync` has **zero external callers** — only defined in `LibraryProvisioningService.cs`

### Non-Goals

- ❌ Migrating existing misplaced anime `.strm` files — `emby-reset.sh` handles dev
- ❌ Runtime AIOStreams failover on network error — backup is parse-time only
- ❌ RSS feed source plumbing — separate sprint

---

## Change 1 of 7 — `PluginConfiguration.cs`: Add `EnableBackupAioStreams`

**File:** `PluginConfiguration.cs`

Find this exact text (lines 75–76):
```csharp
        public string SecondaryManifestUrl { get; set; } = string.Empty;
```

Replace with:
```csharp
        public string SecondaryManifestUrl { get; set; } = string.Empty;

        /// <summary>
        /// When true, SecondaryManifestUrl is used as a fallback if the primary
        /// manifest URL cannot be parsed. When false, SecondaryManifestUrl is ignored.
        /// </summary>
        public bool EnableBackupAioStreams { get; set; } = false;
```

---

## Change 2 of 7 — `Services/AioStreamsClient.cs`: Gate backup behind flag

**File:** `Services/AioStreamsClient.cs`

Find this exact text (lines 617–619):
```csharp
            // Fall back to secondary manifest URL if primary is not provided.
            if (string.IsNullOrWhiteSpace(baseUrl))
                (baseUrl, uuid, token) = TryParseManifestUrl(config.SecondaryManifestUrl);
```

Replace with:
```csharp
            // Fall back to secondary manifest URL only if the user has enabled it.
            if (string.IsNullOrWhiteSpace(baseUrl) && config.EnableBackupAioStreams)
                (baseUrl, uuid, token) = TryParseManifestUrl(config.SecondaryManifestUrl);
```

---

## Change 3 of 7 — `Services/LibraryProvisioningService.cs`: Full file rewrite

**File:** `Services/LibraryProvisioningService.cs`

**Delete the entire file contents and replace with:**

```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Creates and verifies Emby virtual folder libraries for InfiniteDrive.
    /// Idempotent — safe to call on every wizard run.
    /// </summary>
    public class LibraryProvisioningService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<LibraryProvisioningService> _logger;

        public LibraryProvisioningService(
            ILibraryManager libraryManager,
            ILogger<LibraryProvisioningService> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        /// <summary>
        /// Creates disk directories and Emby library entries for all configured paths.
        /// Skips any library whose path already exists in Emby. Safe to call repeatedly.
        /// </summary>
        public async Task EnsureLibrariesProvisionedAsync()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogWarning("[InfiniteDrive] LibraryProvisioningService: config not available");
                return;
            }

            _logger.LogInformation("[InfiniteDrive] Ensuring libraries are provisioned…");

            // Always provision Movies and Shows.
            await ProvisionOneAsync(
                config.LibraryNameMovies ?? "Streamed Movies",
                "movies",
                config.SyncPathMovies);

            await ProvisionOneAsync(
                config.LibraryNameSeries ?? "Streamed Series",
                "tvshows",
                config.SyncPathShows);

            // Anime only if enabled.
            if (config.EnableAnimeLibrary && !string.IsNullOrWhiteSpace(config.SyncPathAnime))
            {
                await ProvisionOneAsync(
                    config.LibraryNameAnime ?? "Streamed Anime",
                    "",           // empty string = mixed/all content types in Emby
                    config.SyncPathAnime);
            }

            _logger.LogInformation("[InfiniteDrive] Library provisioning complete");
        }

        // ── Private ──────────────────────────────────────────────────────────────

        private async Task ProvisionOneAsync(string name, string contentType, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            // Create directory if missing.
            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                    _logger.LogInformation("[InfiniteDrive] Created directory: {Path}", path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] Failed to create directory: {Path}", path);
                return;
            }

            // Check if Emby already has a library pointing at this path.
            var norm = path.TrimEnd('/', '\\');
            var existing = _libraryManager.GetVirtualFolders();
            var alreadyRegistered = existing.Any(f =>
                f.Locations != null &&
                f.Locations.Any(loc =>
                    string.Equals(
                        loc.TrimEnd('/', '\\'),
                        norm,
                        StringComparison.OrdinalIgnoreCase)));

            if (alreadyRegistered)
            {
                _logger.LogInformation(
                    "[InfiniteDrive] Library '{Name}' already exists at {Path} — skipping", name, path);
                return;
            }

            // Create the Emby virtual folder.
            try
            {
                var options = new LibraryOptions
                {
                    ContentType = contentType,
                    PathInfos = new[]
                    {
                        new MediaPathInfo { Path = path }
                    }
                };

                _libraryManager.AddVirtualFolder(name, options, refreshLibrary: false);

                _logger.LogInformation(
                    "[InfiniteDrive] Created Emby library '{Name}' (type='{Type}') at {Path}",
                    name, string.IsNullOrEmpty(contentType) ? "mixed" : contentType, path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[InfiniteDrive] Failed to create library '{Name}'. " +
                    "Create it manually: Emby Dashboard → Libraries → Add Media Library → " +
                    "type '{Type}', path '{Path}'",
                    name, string.IsNullOrEmpty(contentType) ? "mixed" : contentType, path);
            }

            await Task.CompletedTask;
        }
    }
}
```

---

## Change 4 of 7 — `Services/SetupService.cs`: Add ProvisionLibraries endpoint

**File:** `Services/SetupService.cs`

### 4a — Add using directives

Find this exact text at the top of the file:
```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;
```

Replace with:
```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;
```

### 4b — Add request/response classes

Find this exact text:
```csharp
    /// <summary>
    /// Service for setup operations (creating directories, rotating API keys, etc.).
    /// Called by the wizard during initial configuration and user maintenance.
    /// </summary>
    public class SetupService : IService, IRequiresRequest
```

Replace with:
```csharp
    /// <summary>
    /// Request to create Emby virtual folder libraries.
    /// </summary>
    [Route("/InfiniteDrive/Setup/ProvisionLibraries", "POST",
        Summary = "Create Emby library entries for InfiniteDrive paths if they do not exist")]
    public class ProvisionLibrariesRequest : IReturn<ProvisionLibrariesResponse> { }

    /// <summary>Response from library provisioning.</summary>
    public class ProvisionLibrariesResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Service for setup operations (creating directories, rotating API keys, etc.).
    /// Called by the wizard during initial configuration and user maintenance.
    /// </summary>
    public class SetupService : IService, IRequiresRequest
```

### 4c — Update fields and constructor

Find this exact text:
```csharp
        private readonly ILogger<SetupService> _logger;
        private readonly IAuthorizationContext _authCtx;

        public IRequest Request { get; set; } = null!;

        public SetupService(ILogManager logManager, IAuthorizationContext authCtx)
        {
            _logger = new EmbyLoggerAdapter<SetupService>(logManager.GetLogger("InfiniteDrive"));
            _authCtx = authCtx;
        }
```

Replace with:
```csharp
        private readonly ILogger<SetupService> _logger;
        private readonly ILogManager _logManager;
        private readonly IAuthorizationContext _authCtx;
        private readonly ILibraryManager _libraryManager;

        public IRequest Request { get; set; } = null!;

        public SetupService(ILogManager logManager, IAuthorizationContext authCtx, ILibraryManager libraryManager)
        {
            _logManager = logManager;
            _logger = new EmbyLoggerAdapter<SetupService>(logManager.GetLogger("InfiniteDrive"));
            _authCtx = authCtx;
            _libraryManager = libraryManager;
        }
```

### 4d — Add handler method

Find this exact text:
```csharp
        public object Post(CreateDirectoriesRequest request)
```

Insert the following **before** that line (add a blank line after the new block):
```csharp
        /// <summary>
        /// Creates Emby library entries for all configured InfiniteDrive paths.
        /// Idempotent — safe to call on every wizard run.
        /// </summary>
        public async Task<object> Post(ProvisionLibrariesRequest _)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var service = new LibraryProvisioningService(
                _libraryManager,
                new EmbyLoggerAdapter<LibraryProvisioningService>(_logManager.GetLogger("InfiniteDrive")));

            await service.EnsureLibrariesProvisionedAsync();

            return new ProvisionLibrariesResponse
            {
                Success = true,
                Message = "Libraries provisioned"
            };
        }

```

---

## Change 5 of 7 — `Services/StrmWriterService.cs`: Route anime to `SyncPathAnime`

**File:** `Services/StrmWriterService.cs`

Find this exact text (lines 58–83):
```csharp
            if (item.MediaType == "movie")
            {
                if (string.IsNullOrWhiteSpace(config.SyncPathMovies)) return null;
                var folder = Path.Combine(
                    config.SyncPathMovies,
                    SanitisePath(BuildFolderName(item.Title, item.Year, item.ImdbId, item.TmdbId, item.TvdbId, item.MediaType)));
                Directory.CreateDirectory(folder);
                var fileName = $"{SanitisePath(item.Title)}{(item.Year.HasValue ? $" ({item.Year})" : string.Empty)}.strm";
                var path = Path.Combine(folder, fileName);
                WriteStrmFile(path, BuildSignedStrmUrl(config, item.ImdbId, "movie", null, null));
                WriteNfoFileIfEnabled(config, item, path, originSourceType);
                await PersistFirstAddedByUserIdIfNotSetAsync(item, ownerUserId, ct);
                return path;
            }

            // Series — seed S01E01
            if (string.IsNullOrWhiteSpace(config.SyncPathShows)) return null;
            var showDir = Path.Combine(config.SyncPathShows,
                SanitisePath(BuildFolderName(item.Title, item.Year, item.ImdbId, item.TmdbId, item.TvdbId, item.MediaType)));
            var seasonDir = Path.Combine(showDir, "Season 01");
            Directory.CreateDirectory(seasonDir);
            var strmPath = Path.Combine(seasonDir, $"{SanitisePath(item.Title)} S01E01.strm");
            WriteStrmFile(strmPath, BuildSignedStrmUrl(config, item.ImdbId, "series", 1, 1));
            WriteNfoFileIfEnabled(config, item, strmPath, originSourceType);
            await PersistFirstAddedByUserIdIfNotSetAsync(item, ownerUserId, ct);
            return strmPath;
```

Replace with:
```csharp
            // Anime items route to SyncPathAnime (a mixed Emby library) when the anime
            // library is enabled. CatalogType == "anime" is the signal; MediaType tells
            // us whether to use a flat movie structure or a season folder structure.
            var isAnime = string.Equals(item.CatalogType, "anime", StringComparison.OrdinalIgnoreCase);
            if (isAnime && config.EnableAnimeLibrary && !string.IsNullOrWhiteSpace(config.SyncPathAnime))
            {
                var animeFolder = Path.Combine(
                    config.SyncPathAnime,
                    SanitisePath(BuildFolderName(item.Title, item.Year, item.ImdbId, item.TmdbId, item.TvdbId, item.MediaType)));

                if (item.MediaType == "movie")
                {
                    Directory.CreateDirectory(animeFolder);
                    var animeFileName = $"{SanitisePath(item.Title)}{(item.Year.HasValue ? $" ({item.Year})" : string.Empty)}.strm";
                    var animePath = Path.Combine(animeFolder, animeFileName);
                    WriteStrmFile(animePath, BuildSignedStrmUrl(config, item.ImdbId, "movie", null, null));
                    WriteNfoFileIfEnabled(config, item, animePath, originSourceType);
                    await PersistFirstAddedByUserIdIfNotSetAsync(item, ownerUserId, ct);
                    return animePath;
                }
                else
                {
                    var animeSeasonDir = Path.Combine(animeFolder, "Season 01");
                    Directory.CreateDirectory(animeSeasonDir);
                    var animeStrmPath = Path.Combine(animeSeasonDir, $"{SanitisePath(item.Title)} S01E01.strm");
                    WriteStrmFile(animeStrmPath, BuildSignedStrmUrl(config, item.ImdbId, "series", 1, 1));
                    WriteNfoFileIfEnabled(config, item, animeStrmPath, originSourceType);
                    await PersistFirstAddedByUserIdIfNotSetAsync(item, ownerUserId, ct);
                    return animeStrmPath;
                }
            }

            if (item.MediaType == "movie")
            {
                if (string.IsNullOrWhiteSpace(config.SyncPathMovies)) return null;
                var folder = Path.Combine(
                    config.SyncPathMovies,
                    SanitisePath(BuildFolderName(item.Title, item.Year, item.ImdbId, item.TmdbId, item.TvdbId, item.MediaType)));
                Directory.CreateDirectory(folder);
                var fileName = $"{SanitisePath(item.Title)}{(item.Year.HasValue ? $" ({item.Year})" : string.Empty)}.strm";
                var path = Path.Combine(folder, fileName);
                WriteStrmFile(path, BuildSignedStrmUrl(config, item.ImdbId, "movie", null, null));
                WriteNfoFileIfEnabled(config, item, path, originSourceType);
                await PersistFirstAddedByUserIdIfNotSetAsync(item, ownerUserId, ct);
                return path;
            }

            // Series — seed S01E01
            if (string.IsNullOrWhiteSpace(config.SyncPathShows)) return null;
            var showDir = Path.Combine(config.SyncPathShows,
                SanitisePath(BuildFolderName(item.Title, item.Year, item.ImdbId, item.TmdbId, item.TvdbId, item.MediaType)));
            var seasonDir = Path.Combine(showDir, "Season 01");
            Directory.CreateDirectory(seasonDir);
            var strmPath = Path.Combine(seasonDir, $"{SanitisePath(item.Title)} S01E01.strm");
            WriteStrmFile(strmPath, BuildSignedStrmUrl(config, item.ImdbId, "series", 1, 1));
            WriteNfoFileIfEnabled(config, item, strmPath, originSourceType);
            await PersistFirstAddedByUserIdIfNotSetAsync(item, ownerUserId, ct);
            return strmPath;
```

---

## Change 6 of 7 — `Tasks/CatalogSyncTask.cs`: Add anime library warning

**File:** `Tasks/CatalogSyncTask.cs`

Find this exact text (lines 1344–1345):
```csharp
                Check(config.SyncPathMovies, "movies", "Movies");
                Check(config.SyncPathShows,  "tvshows", "TV Shows");
```

Replace with:
```csharp
                Check(config.SyncPathMovies, "movies", "Movies");
                Check(config.SyncPathShows,  "tvshows", "TV Shows");
                if (config.EnableAnimeLibrary)
                    Check(config.SyncPathAnime, "mixed", "Anime");
```

---

## Change 7 of 7 — `Configuration/configurationpage.js`: Wire provisioning into `finishWizard`

**File:** `Configuration/configurationpage.js`

Find this exact text (lines 2364–2398):
```javascript
        if (progressEl) progressEl.style.display = 'block';
        if (wizardNav) wizardNav.style.display = 'none';
        if (msgEl) { msgEl.textContent = 'Saving configuration and starting first sync…'; msgEl.className = 'es-alert es-alert-info'; }
        if (barEl) barEl.style.width = '10%';

        ApiClient.updatePluginConfiguration(pluginId, pluginConfig)
            .then(function() {
                if (barEl) barEl.style.width = '30%';

                // Trigger catalog sync
                return esFetch('/InfiniteDrive/Trigger?task=catalog_sync', {method:'POST'});
            })
            .then(function() {
                if (barEl) barEl.style.width = '50%';

                // Animate sync progress
                animateSyncProgress(view, function() {
                    // Show completion screen after sync
                    setTimeout(function() {
                        if (progressEl) progressEl.style.display = 'none';
                        var completeDiv = q(view, 'es-wizard-complete');
                        if (completeDiv) {
                            completeDiv.style.display = 'block';
                            loadCompletionStats(view);
                        }
                    }, 500);
                });
            })
            .catch(function(err) {
                if (msgEl) {
                    msgEl.textContent = 'Error: ' + (err.message || 'Failed to save configuration');
                    msgEl.className = 'es-alert es-alert-error';
                }
                if (wizardNav) wizardNav.style.display = 'flex';
            });
```

Replace with:
```javascript
        if (progressEl) progressEl.style.display = 'block';
        if (wizardNav) wizardNav.style.display = 'none';
        if (msgEl) { msgEl.textContent = 'Saving configuration…'; msgEl.className = 'es-alert es-alert-info'; }
        if (barEl) barEl.style.width = '10%';

        ApiClient.updatePluginConfiguration(pluginId, pluginConfig)
            .then(function() {
                if (barEl) barEl.style.width = '30%';
                if (msgEl) msgEl.textContent = 'Creating Emby libraries…';

                // Create Emby library entries for all configured paths.
                return esFetch('/InfiniteDrive/Setup/ProvisionLibraries', {method:'POST'});
            })
            .then(function() {
                if (barEl) barEl.style.width = '55%';
                if (msgEl) msgEl.textContent = 'Starting sync…';

                // Trigger catalog sync.
                return esFetch('/InfiniteDrive/Trigger?task=catalog_sync', {method:'POST'});
            })
            .then(function() {
                if (barEl) barEl.style.width = '75%';

                // Animate sync progress then show completion screen.
                animateSyncProgress(view, function() {
                    setTimeout(function() {
                        if (progressEl) progressEl.style.display = 'none';
                        var completeDiv = q(view, 'es-wizard-complete');
                        if (completeDiv) {
                            completeDiv.style.display = 'block';
                            loadCompletionStats(view);
                        }
                    }, 500);
                });
            })
            .catch(function(err) {
                if (msgEl) {
                    msgEl.textContent = 'Error: ' + (err.message || 'Failed to save configuration');
                    msgEl.className = 'es-alert es-alert-error';
                }
                if (wizardNav) wizardNav.style.display = 'flex';
            });
```

---

## Build & Verify

### Step 1 — Build
```
dotnet build -c Release
```
Expected: 0 errors, 0 net-new warnings.

**If build fails with `AddVirtualFolder` — wrong argument count or types:**
The `LibraryOptions` constructor may require explicit initialization. Try changing:
```csharp
                var options = new LibraryOptions
                {
                    ContentType = contentType,
                    PathInfos = new[]
                    {
                        new MediaPathInfo { Path = path }
                    }
                };
```
to:
```csharp
                var options = new LibraryOptions();
                options.ContentType = contentType;
                options.PathInfos = new MediaPathInfo[] { new MediaPathInfo { Path = path } };
```

**If build fails with `AddVirtualFolder` — method not found on interface:**
Replace the entire `_libraryManager.AddVirtualFolder(name, options, refreshLibrary: false);` call with:
```csharp
                _logger.LogWarning(
                    "[InfiniteDrive] Cannot auto-create library '{Name}' — " +
                    "AddVirtualFolder not available. Create manually: " +
                    "Emby Dashboard → Libraries → Add Media Library → type '{Type}', path '{Path}'",
                    name, string.IsNullOrEmpty(contentType) ? "mixed" : contentType, path);
```

### Step 2 — Deploy and smoke test
```
./emby-reset.sh
```

### Step 3 — Grep checklist

| Pattern | Expected count | File |
|---|---|---|
| `EnableBackupAioStreams` | 3 | PluginConfiguration.cs (1 def), AioStreamsClient.cs (1 use), configurationpage.js (2 uses from Sprint 200) |
| `EnsureLibrariesProvisionedAsync` | 2 | LibraryProvisioningService.cs (def + call inside class) |
| `ProvisionLibraries` | 4 | SetupService.cs (request class, response class, Route attr, handler) |
| `ProvisionLibraries` in `.js` | 1 | configurationpage.js |
| `isAnime` | 1 | StrmWriterService.cs |
| `SyncPathAnime` | 4+ | Config (1), StrmWriterService (1), CatalogSyncTask (2), LibraryProvisioningService (1) |
| `Task<bool> EnsureLibraries` | 0 | Confirms old signature is gone |
| `libraries_provisioned` | 0 | Confirms flag file logic is gone |
| `IApplicationPaths` in LibraryProvisioningService | 0 | Confirms old deps removed |

### Step 4 — Manual tests

**Test A: Libraries created after wizard**
1. Complete the wizard with anime library enabled
2. Emby Dashboard → Libraries
3. Assert: "Streamed Movies", "Streamed Series", "Streamed Anime" all appear
4. Re-run wizard → Assert: no duplicate libraries

**Test B: Anime routing**
1. After sync, check filesystem at the `SyncPathAnime` directory
2. Assert: anime series appear as `{Title}/Season 01/{Title} S01E01.strm`
3. Assert: anime movies appear as `{Title}/{Title} (Year).strm`
4. Assert: nothing new appeared in `SyncPathShows` or `SyncPathMovies` for anime catalog items

**Test C: Backup AIOStreams gate**
1. Set `SecondaryManifestUrl` to any URL, leave `EnableBackupAioStreams = false`
2. Set `PrimaryManifestUrl` to a malformed URL
3. Trigger sync → Assert: sync fails cleanly, no fallback to secondary in logs
4. Set `EnableBackupAioStreams = true`, trigger again → Assert: secondary URL is attempted (log line visible)

---

## Rollback

- All changes are service/config layer — no database changes
- Anime `.strm` files misplaced before this sprint remain in `SyncPathShows` — remove manually or run `./emby-reset.sh` on dev
- Emby libraries created by this sprint are not auto-deleted on rollback — remove via Emby Dashboard → Libraries if needed
- To restore silent secondary URL fallback: set `EnableBackupAioStreams = true` in config

## Notes

**Files created:** 0
**Files modified:** 6 (`PluginConfiguration.cs`, `Services/AioStreamsClient.cs`, `Services/LibraryProvisioningService.cs`, `Services/SetupService.cs`, `Services/StrmWriterService.cs`, `Tasks/CatalogSyncTask.cs`, `Configuration/configurationpage.js`)
**Files deleted:** 0

Build: `dotnet build -c Release` — 0 errors, 1 warning (pre-existing)
Grep checklist: All passed

---

## Completion Criteria

- [x] `EnableBackupAioStreams` in `PluginConfiguration.cs`
- [x] `AioStreamsClient` fallback gated behind `EnableBackupAioStreams`
- [x] `LibraryProvisioningService` fully rewritten — no stubs, no flag file, no dead params
- [x] `POST /InfiniteDrive/Setup/ProvisionLibraries` endpoint exists and returns 200
- [x] Anime items with `CatalogType == "anime"` and `EnableAnimeLibrary == true` route to `SyncPathAnime`
- [x] Anime movies use flat folder, anime series use Season 01 subfolder
- [x] `WarnIfLibrariesMissing` checks anime path when `EnableAnimeLibrary` is true
- [x] `finishWizard` JS calls ProvisionLibraries before catalog sync trigger
- [x] `dotnet build -c Release` — 0 errors
- [ ] Libraries appear in Emby after wizard completion (Test A — manual)
- [ ] Anime `.strm` files land in correct path (Test B — manual)
