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
        public int SkippedNoImdb { get; set; }
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
                try { await _cooldown.WaitAsync(CooldownKind.CatalogFetch, ct); }
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

            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrEmpty(item.ImdbId)) { skippedNoImdb++; continue; }

                // Resolve non-tt IDs through IdResolverService (tmdb_, mal:, etc.)
                var resolvedId = item.ImdbId;
                if (_idResolver != null && !item.ImdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var resolved = await _idResolver.ResolveAsync(
                            item.ImdbId, string.Empty, item.MediaType ?? "movie", ct);
                        resolvedId = resolved.CanonicalId;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex,
                            "[UserCatalogSync] IdResolver failed for {RawId}, using native ID",
                            item.ImdbId);
                    }
                }

                if (string.IsNullOrEmpty(resolvedId)) { skippedNoImdb++; continue; }

                var existing = await _db.GetCatalogItemByImdbIdAsync(resolvedId);
                var isNew = existing == null;

                var catalogItem = existing ?? new CatalogItem
                {
                    Id        = Guid.NewGuid().ToString(),
                    ImdbId    = resolvedId,
                    MediaType = item.MediaType ?? "movie",
                    Source    = "external_list",
                };

                catalogItem.Title = item.Title;
                if (item.Year.HasValue) catalogItem.Year = item.Year;
                catalogItem.Source = "external_list";

                await _db.UpsertCatalogItemAsync(catalogItem, ct);

                // Write .strm file
                await _strmWriter.WriteAsync(catalogItem, SourceType.UserRss, catalog.OwnerUserId, ct);

                // Link to this catalog in source_memberships
                await _db.UpsertSourceMembershipWithCatalogAsync(
                    "external_list_" + catalog.OwnerUserId,
                    catalogItem.Id,
                    catalog.Id,
                    ct);

                if (isNew) added++; else updated++;
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
                SkippedNoImdb = skippedNoImdb,
                ElapsedMs    = sw.ElapsedMilliseconds,
            };

            _logger.LogInformation(
                "[UserCatalogSync] {CatalogId} ({Name}): fetched={Fetched} added={Added} updated={Updated} skipped={Skip} elapsed={Ms}ms",
                catalogId, catalog.DisplayName,
                result.Fetched, result.Added, result.Updated, result.SkippedNoImdb, result.ElapsedMs);

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
