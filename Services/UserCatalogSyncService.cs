using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Result of a single user catalog sync operation.
    /// </summary>
    public sealed class UserCatalogSyncResult
    {
        public bool Ok           { get; set; }
        public string CatalogId  { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int Fetched       { get; set; }
        public int Added         { get; set; }
        public int Updated       { get; set; }
        public int Removed       { get; set; }
        public int SkippedNoAioId { get; set; }
        public long ElapsedMs    { get; set; }
        public string? Error     { get; set; }
    }

    /// <summary>
    /// Syncs external list catalogs (MDBList, Trakt, TMDB, AniList) into the catalog_items table.
    /// Used by the CatalogSyncTask backstop and the refresh endpoints.
    /// </summary>
    public class UserCatalogSyncService
    {
        private readonly ILogger<UserCatalogSyncService> _logger;
        private readonly DatabaseManager _db;
        private readonly StrmWriterService _strmWriter;
        private readonly CooldownGate? _cooldown;
        private readonly IdResolverService? _idResolver;

        public UserCatalogSyncService(
            ILogManager logManager,
            DatabaseManager db,
            StrmWriterService strmWriter,
            CooldownGate? cooldown = null,
            IdResolverService? idResolver = null)
        {
            _logger    = new EmbyLoggerAdapter<UserCatalogSyncService>(logManager.GetLogger("InfiniteDrive"));
            _db        = db;
            _strmWriter = strmWriter;
            _cooldown  = cooldown;
            _idResolver = idResolver;
        }

        /// <summary>
        /// Syncs one user catalog. If the catalog is inactive, returns a no-op result.
        /// Never throws — all errors are returned in <see cref="UserCatalogSyncResult.Error"/>.
        /// </summary>
        public async Task<UserCatalogSyncResult> SyncOneAsync(string catalogId, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            var catalog = await _db.GetUserCatalogByIdAsync(catalogId, ct);
            if (catalog == null)
                return Fail(catalogId, "Catalog not found", sw);

            if (!catalog.Active)
            {
                return new UserCatalogSyncResult
                {
                    Ok = true, CatalogId = catalogId, DisplayName = catalog.DisplayName,
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }

            // Throttle via cooldown gate
            if (_cooldown != null)
            {
                try { await _cooldown.WaitAsync(CooldownKind.Default, ct); }
                catch (OperationCanceledException) { return Fail(catalogId, "Cancelled", sw); }
            }

            // Fetch list via ListFetcher
            var config = Plugin.Instance.Configuration;
            var fetchResult = await ListFetcher.FetchAsync(
                catalog.ListUrl,
                config.TraktClientId,
                config.TmdbApiKey,
                _logger,
                ct);

            if (!fetchResult.Ok)
            {
                _logger.LogWarning("[UserCatalogSync] {CatalogId} — {Msg}", catalogId, fetchResult.Error);
                await _db.UpdateUserCatalogSyncStatusAsync(catalogId, DateTimeOffset.UtcNow, fetchResult.Error, ct);
                return Fail(catalogId, fetchResult.Error, sw);
            }

            var items = fetchResult.Items;
            var added = 0;
            var updated = 0;
            var skippedNoImdb = 0;
            var sourceId = "external_list_" + catalog.OwnerUserId;

            // Per-user surfacing: each list item is linked to a per-list playlist so it
            // shows up in the owner's Emby playlist (reconciled from collection_membership).
            var memberships = new List<(string, string?, string, string, string?)>();
            var playlistName = string.IsNullOrWhiteSpace(catalog.DisplayName) ? "My InfiniteDrive" : catalog.DisplayName!;

            // Ensure parent source row exists for FK integrity
            await _db.UpsertSourceAsync(new Source
            {
                Id = sourceId,
                Name = catalog.DisplayName ?? sourceId,
                Type = SourceType.UserRss,
                Enabled = true,
            }, ct);

            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrEmpty(item.AioId)) { skippedNoImdb++; continue; }

                // Resolve non-tt IDs through IdResolverService (tmdb_, mal:, etc.)
                var resolvedId = item.AioId;
                if (_idResolver != null && !item.AioId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var resolved = await _idResolver.ResolveAsync(
                            item.AioId, string.Empty, item.MediaType ?? "movie", ct);
                        resolvedId = resolved.CanonicalId;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex,
                            "[UserCatalogSync] IdResolver failed for {RawId}, using native ID",
                            item.AioId);
                    }
                }

                if (string.IsNullOrEmpty(resolvedId)) { skippedNoImdb++; continue; }

                var existing = await _db.GetCatalogItemByAioIdAsync(resolvedId);
                var isNew = existing == null;

                var catalogItem = existing ?? new CatalogItem
                {
                    Id        = Guid.NewGuid().ToString(),
                    AioId     = resolvedId,
                    MediaType = item.MediaType ?? "movie",
                    Source    = "external_list",
                };

                catalogItem.Title = item.Title;
                if (item.Year.HasValue) catalogItem.Year = item.Year;
                // Only stamp the source on genuinely-new rows. Overwriting an existing
                // item's source breaks the ON CONFLICT(aio_id, source) upsert (the row is
                // reused by id, so a changed source forces an INSERT that collides on id).
                if (isNew) catalogItem.Source = "external_list";
                catalogItem.SourceListId = catalog.Id;

                await _db.UpsertCatalogItemAsync(catalogItem, ct);

                // Write .strm file
                await _strmWriter.WriteAsync(catalogItem, SourceType.UserRss, catalog.OwnerUserId, ct);

                // Surface per-user: link to the owner's per-list playlist.
                memberships.Add((playlistName, null, resolvedId, "external_list", catalog.OwnerUserId));

                if (isNew) added++; else updated++;
            }

            // Persist per-user memberships so the list shows up as the user's playlist.
            if (memberships.Count > 0)
            {
                try { await _db.UpsertCollectionMembershipBatchAsync(memberships, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "[UserCatalogSync] {CatalogId} — membership upsert failed", catalogId); }

                // Immediate surfacing: add already-indexed items to the owner's playlist now.
                // Marvin's CollectionPopulationPass reconciles any not-yet-scanned items later.
                try
                {
                    var playlistService = Plugin.Instance?.PlaylistService;
                    var lm = Plugin.Instance?.LibraryManager;
                    if (playlistService != null && lm != null)
                    {
                        foreach (var m in memberships)
                        {
                            if (ct.IsCancellationRequested) break;
                            var aio = m.Item3;
                            if (string.IsNullOrEmpty(aio) || !aio.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                                continue; // non-tt ids surface via Marvin after id resolution
                            var q = new MediaBrowser.Controller.Entities.InternalItemsQuery
                            {
                                AnyProviderIdEquals = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>
                                {
                                    new System.Collections.Generic.KeyValuePair<string, string>("Imdb", aio)
                                },
                                IncludeItemTypes = new[] { "Movie", "Series" },
                                Limit = 1,
                            };
                            var res = lm.GetItemList(q);
                            if (res != null && res.Length > 0 && res[0] != null)
                                await playlistService.AddItemToPlaylistAsync(playlistName, res[0]!.Id, catalog.OwnerUserId, ct);
                        }
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "[UserCatalogSync] immediate playlist surface failed for {CatalogId}", catalogId); }
            }

            // Sync status
            await _db.UpdateUserCatalogSyncStatusAsync(catalogId, DateTimeOffset.UtcNow, "ok", ct);

            var result = new UserCatalogSyncResult
            {
                Ok = true,
                CatalogId    = catalogId,
                DisplayName  = catalog.DisplayName,
                Fetched      = items.Count,
                Added        = added,
                Updated      = updated,
                SkippedNoAioId = skippedNoImdb,
                ElapsedMs    = sw.ElapsedMilliseconds,
            };

            _logger.LogInformation(
                "[UserCatalogSync] {CatalogId} ({Name}): fetched={Fetched} added={Added} updated={Updated} skipped={Skip} elapsed={Ms}ms",
                catalogId, catalog.DisplayName,
                result.Fetched, result.Added, result.Updated, result.SkippedNoAioId, result.ElapsedMs);

            return result;
        }

        /// <summary>
        /// Syncs all active catalogs owned by the given user, sequentially.
        /// </summary>
        public async Task<IReadOnlyList<UserCatalogSyncResult>> SyncAllForOwnerAsync(
            string ownerUserId,
            CancellationToken ct)
        {
            var catalogs = await _db.GetUserCatalogsByOwnerAsync(ownerUserId, activeOnly: true, ct);
            var results  = new List<UserCatalogSyncResult>(catalogs.Count);

            foreach (var catalog in catalogs)
            {
                if (ct.IsCancellationRequested) break;
                results.Add(await SyncOneAsync(catalog.Id, ct));
            }

            return results;
        }

        /// <summary>
        /// Syncs all active catalogs across all users (called by CatalogSyncTask).
        /// </summary>
        public async Task<IReadOnlyList<UserCatalogSyncResult>> SyncAllAsync(CancellationToken ct)
        {
            var catalogs = await _db.GetAllActiveUserCatalogsAsync(ct);
            var results = new List<UserCatalogSyncResult>(catalogs.Count);

            foreach (var catalog in catalogs)
            {
                if (ct.IsCancellationRequested) break;
                results.Add(await SyncOneAsync(catalog.Id, ct));
            }

            return results;
        }

        // ── Private helpers ────────────────────────────────────────────────────────

        private static UserCatalogSyncResult Fail(string catalogId, string error, Stopwatch sw) =>
            new UserCatalogSyncResult
            {
                Ok        = false,
                CatalogId = catalogId,
                Error     = error,
                ElapsedMs = sw.ElapsedMilliseconds,
            };
    }
}
