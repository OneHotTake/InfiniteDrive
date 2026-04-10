using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Logging;
using EmbyStreams.Models;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
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
    /// Syncs user-owned public RSS catalogs (Trakt / MDBList) into the catalog_items table.
    /// Singleton. Used by both the 6-hour CatalogSyncTask backstop and the
    /// impatient-user POST /User/Catalogs/Refresh endpoint.
    /// </summary>
    public class UserCatalogSyncService
    {
        private readonly ILogger<UserCatalogSyncService> _logger;
        private readonly DatabaseManager _db;
        private readonly StrmWriterService _strmWriter;
        private readonly CooldownGate? _cooldown;

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        public UserCatalogSyncService(
            ILogManager logManager,
            DatabaseManager db,
            StrmWriterService strmWriter,
            CooldownGate? cooldown = null)
        {
            _logger    = new EmbyLoggerAdapter<UserCatalogSyncService>(logManager.GetLogger("EmbyStreams"));
            _db        = db;
            _strmWriter = strmWriter;
            _cooldown  = cooldown;
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

            // Fetch RSS feed
            string xml;
            try
            {
                using var resp = await _http.GetAsync(catalog.RssUrl, ct);
                resp.EnsureSuccessStatusCode();
                xml = await resp.Content.ReadAsStringAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var msg = $"HTTP fetch failed: {ex.Message}";
                _logger.LogWarning("[UserCatalogSync] {CatalogId} — {Msg}", catalogId, msg);
                await _db.UpdateUserCatalogSyncStatusAsync(catalogId, DateTimeOffset.UtcNow, msg, ct);
                return Fail(catalogId, msg, sw);
            }

            // Parse
            var items = RssFeedParser.Parse(xml, _logger, out _, out var skippedNoImdb);

            var added = 0;
            var updated = 0;
            var seenImdbIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rssItem in items)
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrEmpty(rssItem.ImdbId)) continue;

                seenImdbIds.Add(rssItem.ImdbId);

                var existing = await _db.GetCatalogItemByImdbIdAsync(rssItem.ImdbId);
                var isNew = existing == null;

                var catalogItem = existing ?? new CatalogItem
                {
                    Id        = Guid.NewGuid().ToString(),
                    ImdbId    = rssItem.ImdbId,
                    MediaType = "movie", // best guess; IdResolverService will refine on Sprint 160C
                    Source    = "user_rss",
                };

                catalogItem.Title = rssItem.Title;
                if (rssItem.Year.HasValue) catalogItem.Year = rssItem.Year;
                catalogItem.Source = "user_rss";

                await _db.UpsertCatalogItemAsync(catalogItem, ct);

                // Write .strm file
                await _strmWriter.WriteAsync(catalogItem, SourceType.UserRss, catalog.OwnerUserId, ct);

                // Link to this user catalog in source_memberships
                await _db.UpsertSourceMembershipWithCatalogAsync(
                    "user_rss_" + catalog.OwnerUserId,
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
        /// The cooldown gate handles politeness between calls.
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
