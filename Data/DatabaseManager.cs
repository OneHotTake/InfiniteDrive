using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Models;
using InfiniteDrive.Repositories.Interfaces;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using SQLitePCL.pretty;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Data
{
    /// <summary>
    /// Manages the InfiniteDrive SQLite database at
    /// <c>{DataPath}/InfiniteDrive/infinitedrive.db</c>.
    /// Single CREATE TABLE IF NOT EXISTS — no migrations, no schema versioning.
    /// </summary>
    public partial class DatabaseManager : IResolutionCacheRepository
    {
        // ── Constants ───────────────────────────────────────────────────────────

        private const int PlaybackLogMaxRows = 500;

        private static class Tables
        {
            public const string CatalogItems          = "catalog_items";
            public const string StreamResolutionCache = "stream_resolution_cache";
            public const string PlaybackLog           = "playback_log";
            public const string ApiBudget             = "api_budget";
            public const string SyncState             = "sync_state";
            public const string CollectionMembership  = "collection_membership";
            public const string HomeSectionTracking   = "home_section_tracking";
        }

        // ── Fields ──────────────────────────────────────────────────────────────

        private readonly string _dbPath;
        private readonly ILogger _logger;

        // ── Column index caches (populated at init from PRAGMA table_info) ────────
        private static readonly ConcurrentDictionary<string, Dictionary<string, int>> _columnMaps
            = new(StringComparer.OrdinalIgnoreCase);

        // ── Write serialization gate ─────────────────────────────────────────────
        // WAL mode does NOT allow concurrent writers. This gate serializes all
        // write operations to prevent "database is locked" errors during concurrent
        // access (e.g., CatalogSyncTask and CatalogDiscoverService running at startup).
        private static readonly SemaphoreSlim _dbWriteGate = new(1, 1);


        // ── Constructor ─────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a DatabaseManager targeting the given directory.
        /// The database file itself is created by <see cref="Initialise"/>.
        /// </summary>
        /// <param name="dbDirectory">Folder that will contain infinitedrive.db.</param>
        /// <param name="logger">ILogger instance from the plugin.</param>
        public DatabaseManager(string dbDirectory, ILogger logger)
        {
            _logger = logger;
            _dbPath = Path.Combine(dbDirectory, "infinitedrive.db");

            Directory.CreateDirectory(dbDirectory);
        }

        // ── Public initialisation ───────────────────────────────────────────────

        /// <summary>
        /// Opens the database, runs an integrity check, and creates the schema.
        /// If corruption is detected the database file is deleted and recreated.
        /// </summary>
        public void Initialise()
        {
            if (!TryIntegrityCheck())
            {
                _logger.LogWarning(
                    "[InfiniteDrive] Database integrity check failed — deleting and recreating {DbPath}",
                    _dbPath);
                File.Delete(_dbPath);
            }

            using var conn = OpenConnection();
            ApplyPragmas(conn);
            CreateSchema(conn);
            BuildColumnMaps(conn);
        }

        // ── catalog_items repository ────────────────────────────────────────────

        /// <summary>
        /// Inserts or updates a catalog item.  The UNIQUE constraint on
        /// (aio_id, source) drives upsert behaviour.
        /// </summary>
        private const string UpsertCatalogItemSql = @"
            INSERT INTO catalog_items
                (id, aio_id, tmdb_id, unique_ids_json, title, year, media_type,
                 source, source_list_id, seasons_json, strm_path,
                 added_at, updated_at, removed_at,
                 local_path, local_source, item_state, pin_source, pinned_at,
                 enrichment_status, retry_count, next_retry_at,
                 blocked_at, blocked_by, first_added_by_user_id,
                 tvdb_id, raw_meta_json, catalog_type, videos_json, episodes_expanded, last_expanded_at, last_verified_at,
                 source_manifest_url, selected_versions_json, last_version_refresh_at)
            VALUES
                (@id, @aio_id, @tmdb_id, @unique_ids_json, @title, @year, @media_type,
                 @source, @source_list_id, @seasons_json, @strm_path,
                 @added_at, @updated_at, @removed_at,
                 @local_path, @local_source, @item_state, @pin_source, @pinned_at,
                 @enrichment_status, @retry_count, @next_retry_at,
                 @blocked_at, @blocked_by, @first_added_by_user_id,
                 @tvdb_id, @raw_meta_json, @catalog_type, @videos_json, @episodes_expanded, @last_expanded_at, @last_verified_at,
                 @source_manifest_url, @selected_versions_json, @last_version_refresh_at)
            ON CONFLICT(aio_id, source) DO UPDATE SET
                tmdb_id       = excluded.tmdb_id,
                unique_ids_json = COALESCE(excluded.unique_ids_json, catalog_items.unique_ids_json),
                title         = excluded.title,
                year          = excluded.year,
                media_type    = excluded.media_type,
                seasons_json  = COALESCE(excluded.seasons_json, catalog_items.seasons_json),
                strm_path     = COALESCE(excluded.strm_path,    catalog_items.strm_path),
                local_path    = COALESCE(catalog_items.local_path,   excluded.local_path),
                local_source  = COALESCE(catalog_items.local_source, excluded.local_source),
                item_state    = COALESCE(excluded.item_state,    catalog_items.item_state),
                pin_source    = COALESCE(excluded.pin_source,    catalog_items.pin_source),
                pinned_at     = COALESCE(excluded.pinned_at,     catalog_items.pinned_at),
                enrichment_status = COALESCE(excluded.enrichment_status, catalog_items.enrichment_status),
                retry_count   = COALESCE(excluded.retry_count, catalog_items.retry_count),
                next_retry_at = COALESCE(excluded.next_retry_at, catalog_items.next_retry_at),
                blocked_at    = COALESCE(catalog_items.blocked_at, excluded.blocked_at),
                blocked_by    = COALESCE(catalog_items.blocked_by, excluded.blocked_by),
                updated_at    = excluded.updated_at,
                removed_at    = NULL,
                tvdb_id        = COALESCE(catalog_items.tvdb_id,       excluded.tvdb_id),
                catalog_type   = COALESCE(catalog_items.catalog_type,  excluded.catalog_type),
                raw_meta_json  = excluded.raw_meta_json,
                videos_json    = COALESCE(excluded.videos_json, catalog_items.videos_json),
                episodes_expanded = COALESCE(excluded.episodes_expanded, catalog_items.episodes_expanded),
                last_expanded_at = COALESCE(excluded.last_expanded_at, catalog_items.last_expanded_at),
                last_verified_at = excluded.last_verified_at,
                source_manifest_url = COALESCE(excluded.source_manifest_url, catalog_items.source_manifest_url),
                selected_versions_json = COALESCE(excluded.selected_versions_json, catalog_items.selected_versions_json),
                last_version_refresh_at = COALESCE(excluded.last_version_refresh_at, catalog_items.last_version_refresh_at);";

        private void BindCatalogItemParams(IStatement cmd, CatalogItem item)
        {
            BindText(cmd, "@id",             item.Id);
            BindText(cmd, "@aio_id",         item.AioId);
            BindNullableText(cmd, "@tmdb_id",        item.TmdbId);
            BindNullableText(cmd, "@unique_ids_json", item.UniqueIdsJson);
            BindText(cmd, "@title",          item.Title);
            BindNullableInt(cmd,  "@year",           item.Year);
            var rawMediaType = item.MediaType;
            var validMediaType = string.IsNullOrEmpty(rawMediaType) ? "movie" : rawMediaType;
            if (validMediaType != "movie" && validMediaType != "series" && validMediaType != "anime" && validMediaType != "episode" && validMediaType != "other")
                validMediaType = "movie";
            BindText(cmd, "@media_type",     validMediaType);
            BindText(cmd, "@source",         item.Source);
            BindNullableText(cmd, "@source_list_id", item.SourceListId);
            BindNullableText(cmd, "@seasons_json",   item.SeasonsJson);
            BindNullableText(cmd, "@strm_path",      item.StrmPath);
            BindText(cmd, "@added_at",       string.IsNullOrEmpty(item.AddedAt) ? DateTime.UtcNow.ToString("o") : item.AddedAt);
            BindText(cmd, "@updated_at",     string.IsNullOrEmpty(item.UpdatedAt) ? DateTime.UtcNow.ToString("o") : item.UpdatedAt);
            BindNullableText(cmd, "@removed_at",     item.RemovedAt);
            BindNullableText(cmd, "@local_path",     item.LocalPath);
            BindNullableText(cmd, "@local_source",   item.LocalSource);
            cmd.BindParameters["@item_state"].Bind((int)item.ItemState);
            BindNullableText(cmd, "@pin_source",     item.PinSource);
            BindNullableText(cmd, "@pinned_at",      item.PinnedAt);
            BindNullableText(cmd, "@enrichment_status", item.EnrichmentStatus);
            BindInt(cmd,         "@retry_count",    item.RetryCount);
            if (item.NextRetryAt.HasValue)
                cmd.BindParameters["@next_retry_at"].Bind(item.NextRetryAt.Value);
            else
                cmd.BindParameters["@next_retry_at"].BindNull();
            BindNullableText(cmd, "@blocked_at", item.BlockedAt);
            BindNullableText(cmd, "@blocked_by", item.BlockedBy);
            BindNullableText(cmd, "@first_added_by_user_id", item.FirstAddedByUserId);
            BindNullableText(cmd, "@tvdb_id", item.TvdbId);
            BindNullableText(cmd, "@raw_meta_json", item.RawMetaJson);
            BindNullableText(cmd, "@catalog_type", item.CatalogType);
            BindNullableText(cmd, "@videos_json", item.VideosJson);
            BindNullableInt(cmd, "@episodes_expanded", item.EpisodesExpanded.HasValue ? (item.EpisodesExpanded.Value ? 1 : 0) : null);
            if (item.LastExpandedAt.HasValue)
                cmd.BindParameters["@last_expanded_at"].Bind(item.LastExpandedAt.Value);
            else
                cmd.BindParameters["@last_expanded_at"].BindNull();
            if (item.LastVerifiedAt.HasValue)
                cmd.BindParameters["@last_verified_at"].Bind(item.LastVerifiedAt.Value);
            else
                cmd.BindParameters["@last_verified_at"].BindNull();
            BindNullableText(cmd, "@source_manifest_url", item.SourceManifestUrl);
            BindNullableText(cmd, "@selected_versions_json", item.SelectedVersionsJson);
            BindNullableText(cmd, "@last_version_refresh_at", item.LastVersionRefreshAt);
        }

        public async Task UpsertCatalogItemAsync(CatalogItem item, CancellationToken cancellationToken = default)
        {
            await ExecuteWriteAsync(UpsertCatalogItemSql, cmd => BindCatalogItemParams(cmd, item));
        }

        /// <summary>
        /// Batch upsert: wraps all items in a single SQLite transaction.
        /// Orders of magnitude faster than calling UpsertCatalogItemAsync in a loop.
        /// </summary>
        public async Task BulkUpsertCatalogItemsAsync(IEnumerable<CatalogItem> items, CancellationToken cancellationToken = default)
        {
            var itemList = items as IList<CatalogItem> ?? items.ToList();
            if (itemList.Count == 0) return;

            await _dbWriteGate.WaitAsync(cancellationToken);
            try
            {
                using var conn = OpenConnection();
                conn.RunInTransaction(c =>
                {
                    foreach (var item in itemList)
                    {
                        using var stmt = c.PrepareStatement(UpsertCatalogItemSql);
                        BindCatalogItemParams(stmt, item);
                        while (stmt.MoveNext()) { }
                    }
                });
            }
            finally
            {
                _dbWriteGate.Release();
            }
        }

        /// <summary>
        /// Sets first_added_by_user_id only if not already set (first-writer-wins).
        /// Called by StrmWriterService when writing .strm files for user-added items.
        /// </summary>
        public async Task SetFirstAddedByUserIdIfNotSetAsync(
            string mediaItemId,
            string userId,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET first_added_by_user_id = @user_id
                WHERE id = @media_id
                  AND first_added_by_user_id IS NULL;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@media_id", mediaItemId);
                BindText(cmd, "@user_id", userId);
            }, cancellationToken);
        }

        /// <summary>
        /// Returns all active (non-removed) catalog items.
        /// </summary>
        public async Task<List<CatalogItem>> GetActiveCatalogItemsAsync()
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE removed_at IS NULL
                  AND blocked_at IS NULL;";

            return await QueryListAsync(sql, null, ReadCatalogItem);
        }

        /// <summary>
        /// Returns the first active catalog item with the specified IMDB ID, or null.
        /// </summary>
        public async Task<CatalogItem?> GetCatalogItemByAioIdAsync(string aioId)
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE aio_id = @aio_id AND removed_at IS NULL
                LIMIT 1;";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@aio_id", aioId),
                ReadCatalogItem);
        }

        /// <summary>
        /// Returns catalog items in the specified state, bounded by limit.
        /// Used by RefreshTask Notify and Verify steps.
        /// </summary>
        public async Task<List<CatalogItem>> GetCatalogItemsByStateAsync(
            ItemState state,
            int limit,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE item_state = @state AND removed_at IS NULL
                LIMIT @limit;";

            return await QueryListAsync(sql, cmd =>
            {
                BindInt(cmd, "@state", (int)state);
                BindInt(cmd, "@limit", limit);
            }, ReadCatalogItem);
        }

        /// <summary>
        /// Returns catalog item by its .strm file path, or null.
        /// Used by auto-pin on playback to find the item being played.
        /// </summary>
        public async Task<CatalogItem?> GetCatalogItemByStrmPathAsync(string strmPath)
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE strm_path = @strm_path AND removed_at IS NULL
                LIMIT 1;";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@strm_path", strmPath),
                ReadCatalogItem);
        }


        /// <summary>
        /// Synchronous version of GetCatalogItemByAioIdAsync for use in
        /// non-async contexts (e.g. GetEpisodeCountForSeason).
        /// </summary>
        public CatalogItem? GetCatalogItemByAioIdSync(string aioId)
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE aio_id = @aio_id AND removed_at IS NULL
                LIMIT 1;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindText(stmt, "@aio_id", aioId);
            foreach (var row in stmt.AsRows())
                return ReadCatalogItem(row);
            return null;
        }
        /// <summary>
        /// Returns all active catalog items from a specific source.
        /// </summary>
        public async Task<List<CatalogItem>> GetCatalogItemsBySourceAsync(string source)
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE source = @source AND removed_at IS NULL;";

            return await QueryListAsync(sql,
                cmd => BindText(cmd, "@source", source),
                ReadCatalogItem);
        }

        /// <summary>
        /// Compares <paramref name="currentAioIds"/> against the active rows for
        /// <paramref name="source"/> in the database.  Any row whose AIO ID is no
        /// longer present in <paramref name="currentAioIds"/> is soft-deleted by
        /// setting <c>removed_at = now()</c>.
        ///
        /// Sprint 302-06: Only removes items not verified in >7 days. Items
        /// that were recently verified are kept to handle transient source errors.
        ///
        /// Returns the list of <c>strm_path</c> values for the removed rows so the
        /// caller can delete the files from disk.
        /// </summary>
        public async Task<List<string>> PruneSourceAsync(
            string          source,
            HashSet<string> currentAioIds,
            CancellationToken cancellationToken = default)
        {
            var existing = await GetCatalogItemsBySourceAsync(source);
            var sevenDaysAgo = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds();

            // Sprint 302-06: Only remove items that:
            // 1. Are not in current catalog AND
            // 2. Have not been verified in >7 days OR never verified
            var toRemove = existing.Where(x =>
                !currentAioIds.Contains(x.AioId) &&
                (x.LastVerifiedAt == null || x.LastVerifiedAt < sevenDaysAgo)
            ).ToList();

            if (toRemove.Count == 0)
                return new List<string>();

            // Batch update all items in single query (fixes N+1)
            var aioIds = toRemove.Select(x => x.AioId).ToList();
            var idsParam = string.Join(",", aioIds.Select((_, i) => $"@id{i}"));

            var sql = $@"
                UPDATE catalog_items
                SET removed_at = datetime('now')
                WHERE aio_id IN ({idsParam}) AND source = @source;";

            var removed = new List<string>();
            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@source", source);
                for (int i = 0; i < aioIds.Count; i++)
                {
                    BindText(cmd, $"@id{i}", aioIds[i]);
                }
            });

            // Collect strm paths for file deletion
            foreach (var item in toRemove)
            {
                if (!string.IsNullOrEmpty(item.StrmPath))
                    removed.Add(item.StrmPath);
            }

            return removed;
        }

        /// <summary>
        /// Updates last_verified_at for items found in catalog sync.
        /// Used for safe removal: items are only pruned if they've been missing
        /// for >7 days.
        /// </summary>
        public async Task UpdateLastVerifiedAtAsync(
            HashSet<string> aioIds,
            string source,
            CancellationToken cancellationToken = default)
        {
            if (aioIds.Count == 0)
                return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var idsParam = string.Join(",", aioIds.Select((_, i) => $"@id{i}"));

            var sql = $@"
                UPDATE catalog_items
                SET last_verified_at = @verified_at
                WHERE aio_id IN ({idsParam}) AND source = @source;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindInt(cmd, "@verified_at", (int)now);
                BindText(cmd, "@source", source);
                for (int i = 0; i < aioIds.Count; i++)
                {
                    BindText(cmd, $"@id{i}", aioIds.ElementAt(i));
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Soft-deletes an item by setting removed_at to now.
        /// The row is never physically deleted (audit trail).
        /// </summary>
        public async Task MarkCatalogItemRemovedAsync(string aioId, string source, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET removed_at = datetime('now')
                WHERE aio_id = @aio_id AND source = @source;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@aio_id", aioId);
                BindText(cmd, "@source",  source);
            });
        }

        /// <summary>
        /// Updates the strm_path for an item identified by (aio_id, source).
        /// </summary>
        public async Task UpdateStrmPathAsync(string aioId, string source, string strmPath, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET strm_path  = @strm_path,
                    updated_at = datetime('now')
                WHERE aio_id = @aio_id AND source = @source;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@strm_path", strmPath);
                BindText(cmd, "@aio_id",    aioId);
                BindText(cmd, "@source",    source);
            });
        }

        /// <summary>
        /// Records where a catalog item currently lives on this server.
        /// Called by <c>CatalogSyncTask</c> after each sync run.
        /// </summary>
        /// <param name="aioId">AIOStreams identifier.</param>
        /// <param name="source">Source key (e.g. <c>aiostreams</c>, <c>trakt</c>).</param>
        /// <param name="localPath">Absolute path to the file (real media or .strm).</param>
        /// <param name="localSource">
        /// <c>library</c> if the file is an existing media file the user already owns,
        /// <c>strm</c> if the plugin wrote it.
        /// </param>
        public async Task UpdateLocalPathAsync(
            string aioId, string source, string? localPath, string? localSource,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET local_path   = @local_path,
                    local_source = @local_source,
                    updated_at   = datetime('now')
                WHERE aio_id = @aio_id AND source = @source;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindNullableText(cmd, "@local_path",   localPath);
                BindNullableText(cmd, "@local_source", localSource);
                BindText(cmd, "@aio_id", aioId);
                BindText(cmd, "@source",  source);
            });
        }

        /// <summary>
        /// Returns the number of active catalog items with a given <c>local_source</c> value.
        /// Pass <c>library</c> or <c>strm</c>.
        /// </summary>
        public Task<int> GetCatalogItemCountByLocalSourceAsync(string localSource)
        {
            const string sql = @"
                SELECT COUNT(*)
                FROM catalog_items
                WHERE local_source = @local_source AND removed_at IS NULL;";

            return QueryScalarIntAsync(sql, cmd => BindText(cmd, "@local_source", localSource));
        }

        /// <summary>
        /// Returns the number of active catalog items that were previously managed
        /// as .strm files but have since been re-adopted into the user's real library.
        /// Identified by having both a <c>strm_path</c> and <c>local_source='library'</c>.
        /// </summary>
        public Task<int> GetReadoptedCountAsync()
        {
            const string sql = @"
                SELECT COUNT(*)
                FROM catalog_items
                WHERE local_source = 'library'
                  AND strm_path IS NOT NULL
                  AND removed_at IS NULL;";

            return QueryScalarIntAsync(sql);
        }

        /// <summary>
        /// Returns all active catalog items with the given <c>local_source</c> value.
        /// Used to find library-tracked items whose original file may have gone missing.
        /// </summary>
        public async Task<List<CatalogItem>> GetItemsByLocalSourceAsync(string localSource)
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE local_source = @local_source AND removed_at IS NULL;";

            return await QueryListAsync(sql,
                cmd => BindText(cmd, "@local_source", localSource),
                ReadCatalogItem);
        }

        /// <summary>
        /// Returns all active catalog items that have no .strm file written yet
        /// (strm_path IS NULL) and are not already adopted from the user's library.
        /// Used by <see cref="Tasks.CatalogSyncTask"/> as a catch-up pass so that
        /// items fetched in a prior run (before sync paths were configured) still
        /// get their .strm files written on the next run.
        /// </summary>
        /// <summary>Returns the filesystem path to the SQLite database file.</summary>
        public string GetDatabasePath() => _dbPath;

        /// <summary>
        /// Deletes all rows from <c>resolution_cache</c> (full cache wipe).
        /// Call <see cref="VacuumAsync"/> afterwards to reclaim disk space.
        /// </summary>
        public async Task ClearResolutionCacheAsync(CancellationToken cancellationToken = default)
        {
            await ExecuteWriteAsync("DELETE FROM stream_resolution_cache;", _ => { }, cancellationToken);
        }

        /// <summary>
        /// Sprint 311: Deletes failed resolution cache entries (no_streams sentinels) for a specific IMDb ID.
        /// Returns the number of rows deleted.
        /// </summary>
        public async Task<int> ClearFailedSentinelAsync(string aioId, CancellationToken cancellationToken = default)
        {
            const string countSql = @"
                SELECT COUNT(*) FROM stream_resolution_cache
                WHERE aio_id = @aio_id AND status = 'failed';";

            const string deleteSql = @"
                DELETE FROM stream_resolution_cache
                WHERE aio_id = @aio_id AND status = 'failed';";

            int count;
            using (var conn = OpenConnection())
            {
                using var stmt = conn.PrepareStatement(countSql);
                BindText(stmt, "@aio_id", aioId);
                count = 0;
                foreach (var row in stmt.AsRows())
                    count = row.GetInt(0);
            }

            if (count > 0)
                await ExecuteWriteAsync(deleteSql, cmd => BindText(cmd, "@aio_id", aioId), cancellationToken);

            return count;
        }

        /// <summary>
        /// Looks up a cached stream for the given (imdb, season, episode) triple.
        /// Returns null on cache miss. Reads rank-0 from stream_resolution_cache.
        /// </summary>
        public async Task<ResolutionEntry?> GetCachedStreamAsync(
            string aioId, int? season, int? episode)
        {
            const string sql = @"
                SELECT id, aio_id, season, episode, url, quality_tier, file_name, file_size,
                       bitrate_kbps, info_hash, is_cached, status, resolved_at, expires_at,
                       play_count, last_played_at, retry_count
                FROM stream_resolution_cache
                WHERE aio_id = @aio_id
                  AND rank = 0
                  AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
                  AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL))
                LIMIT 1;";

            return await QuerySingleAsync(sql, cmd =>
            {
                BindText(cmd, "@aio_id", aioId);
                BindNullableInt(cmd, "@season",  season);
                BindNullableInt(cmd, "@episode", episode);
            }, ReadResolutionEntryFromCache);
        }

        /// <summary>
        /// Increments play_count and updates last_played_at for rank-0 entries.
        /// </summary>
        public async Task IncrementPlayCountAsync(
            string aioId, int? season, int? episode, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE stream_resolution_cache
                SET play_count     = play_count + 1,
                    last_played_at = datetime('now')
                WHERE aio_id = @aio_id
                  AND rank = 0
                  AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
                  AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL));";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@aio_id", aioId);
                BindNullableInt(cmd, "@season",  season);
                BindNullableInt(cmd, "@episode", episode);
            }, cancellationToken);
        }

        /// <summary>
        /// Marks rank-0 entries as <c>failed</c> and increments retry_count.
        /// </summary>
        public async Task MarkStreamFailedAsync(
            string aioId, int? season, int? episode, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE stream_resolution_cache
                SET status      = 'failed',
                    retry_count = retry_count + 1,
                    resolved_at = datetime('now')
                WHERE aio_id = @aio_id
                  AND rank = 0
                  AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
                  AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL));";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@aio_id", aioId);
                BindNullableInt(cmd, "@season",  season);
                BindNullableInt(cmd, "@episode", episode);
            }, cancellationToken);
        }

        /// <summary>
        /// Returns all valid, unexpired rank-0 entries for dead-link scan.
        /// </summary>
        public Task<List<ResolutionEntry>> GetValidCacheEntriesAsync()
        {
            const string sql = @"
                SELECT id, aio_id, season, episode, url, quality_tier, file_name, file_size,
                       bitrate_kbps, info_hash, is_cached, status, resolved_at, expires_at,
                       play_count, last_played_at, retry_count
                FROM stream_resolution_cache
                WHERE status = 'valid'
                  AND rank = 0
                  AND expires_at > datetime('now')
                  AND url IS NOT NULL
                  AND url != ''
                ORDER BY expires_at ASC;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);

            var rows = new List<ResolutionEntry>();
            foreach (var row in stmt.AsRows())
            {
                rows.Add(ReadResolutionEntryFromCache(row));
            }
            return Task.FromResult(rows);
        }

        /// <summary>
        /// Marks rank-0 entries as stale.
        /// </summary>
        public async Task MarkStreamStaleAsync(
            string aioId, int? season, int? episode, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE stream_resolution_cache
                SET status = 'stale', updated_at = datetime('now')
                WHERE aio_id = @aio_id
                  AND rank = 0
                  AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
                  AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL));";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@aio_id", aioId);
                BindNullableInt(cmd, "@season",  season);
                BindNullableInt(cmd, "@episode", episode);
            }, cancellationToken);
        }

        /// <summary>
        /// Clears the <c>last_sync_at</c> timestamp for all sources so the interval
        /// guard is bypassed on the next catalog sync run.
        /// </summary>
        public async Task ResetSyncIntervalsAsync(CancellationToken cancellationToken = default)
        {
            await ExecuteWriteAsync(
                "UPDATE sync_state SET last_sync_at = NULL, consecutive_failures = 0;",
                _ => { }, cancellationToken);
        }

        /// <summary>
        /// Runs SQLite VACUUM to reclaim space after bulk deletes.
        /// This is a full-database operation — avoid calling during active playback.
        /// </summary>
        public async Task VacuumAsync(CancellationToken cancellationToken = default)
        {
            await ExecuteWriteAsync("VACUUM;", _ => { }, cancellationToken);
        }

        /// <summary>
        /// Hard-deletes all rows from <c>catalog_items</c>, <c>sync_state</c>, and
        /// clears the stream_candidates table.
        /// The <c>resolution_cache</c> is intentionally preserved so that cached URLs
        /// can be reused after a re-sync.
        ///
        /// Call <see cref="VacuumAsync"/> and then trigger a catalog sync afterwards
        /// to repopulate from configured sources.
        ///
        /// Returns the list of <c>strm_path</c> values that were stored in the catalog
        /// so the caller can delete the physical files from disk.
        /// </summary>
        public async Task<List<string>> PurgeCatalogAsync(CancellationToken cancellationToken = default)
        {
            // Collect all strm_path values before deletion so the caller can wipe disk files.
            const string selectSql = @"
                SELECT strm_path FROM catalog_items
                WHERE strm_path IS NOT NULL AND strm_path != '';";

            var strmPaths = await QueryListAsync(selectSql, _ => { }, row =>
            {
                var path = row.GetString(0);
                return string.IsNullOrEmpty(path) ? null! : path;
            });
            strmPaths.RemoveAll(string.IsNullOrEmpty);

            // Hard-delete catalog rows, sync tracking, and orphaned candidates.
            // stream_candidates rows are keyed by (imdb_id, season, episode) which
            // overlap with resolution_cache — delete them to avoid orphaned rows since
            // the catalog is being wiped.
            await ExecuteWriteAsync("DELETE FROM catalog_items;",     _ => { }, cancellationToken);
            await ExecuteWriteAsync("DELETE FROM sync_state;",        _ => { }, cancellationToken);
            await ExecuteWriteAsync("DELETE FROM stream_candidates;", _ => { }, cancellationToken);

            return strmPaths;
        }

        /// <summary>
        /// Hard-deletes all rows from every table: catalog, cache, candidates,
        /// playback log, client compat, API budget, and sync state.
        /// The database schema (table structure) is preserved.
        ///
        /// After this call, run <see cref="VacuumAsync"/> and then trigger a full
        /// catalog sync to return to a working state.
        ///
        /// Returns the list of <c>strm_path</c> values that were stored in the catalog
        /// so the caller can delete the physical files from disk.
        /// </summary>
        public async Task<List<string>> ResetAllAsync(CancellationToken cancellationToken = default)
        {
            // Collect strm_path values before clearing (same as PurgeCatalogAsync).
            const string selectSql = @"
                SELECT strm_path FROM catalog_items
                WHERE strm_path IS NOT NULL AND strm_path != '';";

            var strmPaths = await QueryListAsync(selectSql, _ => { }, row =>
            {
                var path = row.GetString(0);
                return string.IsNullOrEmpty(path) ? null! : path;
            });
            strmPaths.RemoveAll(string.IsNullOrEmpty);

            await ExecuteWriteAsync("DELETE FROM catalog_items;",            _ => { }, cancellationToken);
            await ExecuteWriteAsync("DELETE FROM stream_resolution_cache;", _ => { }, cancellationToken);
            await ExecuteWriteAsync("DELETE FROM playback_log;",            _ => { }, cancellationToken);
            await ExecuteWriteAsync("DELETE FROM api_budget;",              _ => { }, cancellationToken);
            await ExecuteWriteAsync("DELETE FROM sync_state;",              _ => { }, cancellationToken);

            return strmPaths;
        }

        /// <summary>
        /// Persists a key-value pair to the plugin_metadata table.
        /// Uses UPSERT semantics: updates value if key exists, inserts if not.
        /// Sprint 102A-02: Plugin metadata table and persistence.
        /// </summary>
        public async Task PersistMetadataAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            var safeValue = string.IsNullOrEmpty(value) ? "" : value;
            await ExecuteWriteAsync(
                "INSERT INTO plugin_metadata (key, value, updated_at) VALUES (@key, @value, @updatedAt) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;",
                cmd =>
                {
                    BindText(cmd, "@key", key);
                    BindText(cmd, "@value", safeValue);
                    BindText(cmd, "@updatedAt", DateTimeOffset.UtcNow.ToString("o"));
                },
                cancellationToken);
        }

        /// <summary>
        /// Retrieves a value from the plugin_metadata table.
        /// Returns null if key not found.
        /// Sprint 102A-02: Plugin metadata table and persistence.
        /// </summary>
        public string? GetMetadata(string key)
        {
            const string sql = "SELECT value FROM plugin_metadata WHERE key = @key;";
            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindText(stmt, "@key", key);
            foreach (var row in stmt.AsRows())
                return row.GetString(0);
            return null;
        }

        // Sprint 350: provider state persistence helpers
        public async Task SetActiveProviderAsync(string provider, CancellationToken ct = default)
        {
            await PersistMetadataAsync("active_provider", provider, ct);
        }

        public string? GetActiveProvider()
        {
            return GetMetadata("active_provider");
        }

        public async Task SetCircuitBreakerStateAsync(string json, CancellationToken ct = default)
        {
            await PersistMetadataAsync("circuit_breaker_state", json, ct);
        }

        public string? GetCircuitBreakerState()
        {
            return GetMetadata("circuit_breaker_state");
        }

        /// <summary>
        /// Returns the sum of all resurrection_count values across active catalog items.
        /// Shown on the health dashboard.
        /// </summary>
        public Task<int> GetTotalResurrectionCountAsync()
        {
            const string sql = @"
                SELECT COALESCE(SUM(resurrection_count), 0)
                FROM catalog_items
                WHERE removed_at IS NULL;";

            return QueryScalarIntAsync(sql);
        }

        /// <summary>
        /// Returns all active series catalog items that have no episode data yet
        /// (<c>seasons_json</c> is NULL or empty).
        /// </summary>
        public async Task<List<CatalogItem>> GetSeriesWithoutSeasonsJsonAsync()
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE media_type = 'series'
                  AND removed_at IS NULL
                  AND (seasons_json IS NULL OR seasons_json = '');";

            return await QueryListAsync(sql, null, ReadCatalogItem);
        }

        /// <summary>
        /// Returns all <c>media_items</c> rows for series that have been indexed by Emby
        /// (emby_item_id is set) and have a strm_path.
        /// </summary>
        public async Task<List<MediaItem>> GetIndexedSeriesAsync(CancellationToken ct = default)
        {
            const string sql = @"
                SELECT mi.id, mi.primary_id_type, mi.primary_id, mi.media_type, mi.title, mi.year,
                       mi.status, mi.failure_reason, mi.saved, mi.saved_at,
                       mi.blocked, mi.blocked_at, mi.created_at, mi.updated_at, mi.grace_started_at,
                       mi.superseded, mi.superseded_conflict, mi.superseded_at,
                       mi.emby_item_id, mi.emby_indexed_at, mi.strm_path,
                       mi.watch_progress_pct, mi.favorited
                FROM media_items mi
                WHERE mi.media_type = 'series'
                  AND mi.emby_item_id IS NOT NULL AND mi.emby_item_id != ''
                  AND mi.strm_path IS NOT NULL AND mi.strm_path != '';";

            return await QueryListAsync(sql, null, ReadMediaItem);
        }

        /// <summary>
        /// Returns series catalog items where <c>seasons_json</c> contains gap data
        /// (at least one season with <c>missingEpisodeNumbers</c>).
        /// </summary>
        public async Task<List<CatalogItem>> GetSeriesWithGapsAsync(int limit, CancellationToken ct = default)
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE media_type IN ('series', 'anime')
                  AND seasons_json IS NOT NULL
                  AND seasons_json != ''
                  AND seasons_json != '[]'
                  AND json_extract(seasons_json, '$[0].missingEpisodeNumbers') IS NOT NULL
                  AND strm_path IS NOT NULL
                  AND removed_at IS NULL
                LIMIT @limit;";

            return await QueryListAsync(sql,
                cmd => BindInt(cmd, "@limit", limit),
                ReadCatalogItem);
        }

        /// <summary>
        /// Updates the <c>seasons_json</c> column for a catalog item.
        /// </summary>
        public async Task UpdateSeasonsJsonAsync(string aioId, string source, string seasonsJson, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET seasons_json = @seasons_json,
                    updated_at   = datetime('now')
                WHERE aio_id = @aio_id AND source = @source;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@seasons_json", seasonsJson);
                BindText(cmd, "@aio_id",       aioId);
                BindText(cmd, "@source",       source);
            }, cancellationToken);
        }

        /// <summary>
        /// Parses the unique_ids_json column into a dictionary for fast lookup.
        /// Returns empty dictionary if the JSON is null or malformed.
        /// Format: [{"provider":"imdb","id":"tt1160419"},{"provider":"kitsu","id":"48363"}]
        /// </summary>
        public Dictionary<string, string> ParseUniqueIdsJson(string? uniqueIdsJson)
        {
            if (string.IsNullOrEmpty(uniqueIdsJson))
                return new Dictionary<string, string>();

            try
            {
                var ids = System.Text.Json.JsonSerializer.Deserialize<UniqueId[]>(uniqueIdsJson);
                if (ids == null) return new Dictionary<string, string>();

                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var uid in ids)
                    dict[uid.Provider] = uid.Id;
                return dict;
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }

        private record UniqueId(string Provider, string Id);

        /// <summary>
        /// Returns a catalog item by any provider ID (not just IMDB).
        /// This enables lookup by Kitsu, AniList, MAL, TMDB, etc.
        /// Uses JSON search on unique_ids_json column.
        /// </summary>
        public async Task<CatalogItem?> GetCatalogItemByProviderIdAsync(string provider, string id)
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE removed_at IS NULL
                  AND EXISTS (
                    SELECT 1 FROM json_each(unique_ids_json)
                    WHERE lower(json_extract(json_each.value, '$.provider')) = lower(@provider)
                      AND lower(json_extract(json_each.value, '$.id')) = lower(@id)
                  )
                LIMIT 1;";

            return await QuerySingleAsync(sql,
                cmd => {
                    BindText(cmd, "@provider", provider.ToLower());
                    BindText(cmd, "@id", id);
                },
                ReadCatalogItem);
        }

        /// <summary>
        /// Returns just the raw_meta_json string for a catalog item matched by provider ID.
        /// Skips the full CatalogItem mapper — one column, no index fragility.
        /// </summary>
        public async Task<string?> GetRawMetaJsonByProviderIdAsync(string provider, string id)
        {
            const string sql = @"
                SELECT raw_meta_json FROM catalog_items
                WHERE removed_at IS NULL
                  AND EXISTS (
                    SELECT 1 FROM json_each(unique_ids_json)
                    WHERE lower(json_extract(json_each.value, '$.provider')) = lower(@provider)
                      AND lower(json_extract(json_each.value, '$.id')) = lower(@id)
                  )
                LIMIT 1";

            return await QuerySingleAsync(sql,
                cmd =>
                {
                    BindText(cmd, "@provider", provider);
                    BindText(cmd, "@id", id);
                },
                row => row.IsDBNull(0) ? null! : row.GetString(0));
        }

        /// <summary>
        /// Last-resort catalog lookup by title (and optionally year / mediaType).
        /// Tries exact match first, then LIKE fallback.
        /// </summary>
        public async Task<CatalogItem?> GetCatalogItemByTitleAsync(string title, int? year, string? mediaType)
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE removed_at IS NULL
                  AND title = @title
                  AND (@year IS NULL OR year = @year)
                  AND (@mediaType IS NULL OR media_type = @mediaType)
                LIMIT 1";

            var result = await QuerySingleAsync(sql,
                cmd =>
                {
                    BindText(cmd, "@title", title);
                    BindNullableInt(cmd, "@year", year);
                    BindNullableText(cmd, "@mediaType", mediaType);
                },
                ReadCatalogItem).ConfigureAwait(false);

            if (result != null) return result;

            const string sqlLike = @"
                SELECT * FROM catalog_items
                WHERE removed_at IS NULL
                  AND title LIKE '%' || @title || '%'
                  AND (@mediaType IS NULL OR media_type = @mediaType)
                LIMIT 1";

            return await QuerySingleAsync(sqlLike,
                cmd =>
                {
                    BindText(cmd, "@title", title);
                    BindNullableText(cmd, "@mediaType", mediaType);
                },
                ReadCatalogItem).ConfigureAwait(false);
        }

        // stream_resolution_cache methods moved to DatabaseManager.StreamCache.cs

        // Operations methods moved to DatabaseManager.Operations.cs

        // ── Private: schema creation ────────────────────────────────────────────

        private void ApplyPragmas(IDatabaseConnection conn)
        {
            conn.Execute("PRAGMA journal_mode=WAL;");
            conn.Execute("PRAGMA foreign_keys=ON;");
            conn.Execute("PRAGMA synchronous=NORMAL;");
            conn.Execute("PRAGMA busy_timeout=30000;");

            // Verify and log WAL mode status
            try
            {
                using var stmt = conn.PrepareStatement("PRAGMA journal_mode;");
                var journalMode = "unknown";
                foreach (var row in stmt.AsRows())
                {
                    if (!row.IsDBNull(0))
                        journalMode = row.GetString(0);
                    break;
                }

                if (string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "[InfiniteDrive] Database PRAGMA configured: journal_mode=WAL, foreign_keys=ON, synchronous=NORMAL, busy_timeout=30000");
                }
                else
                {
                    _logger.LogWarning(
                        "[InfiniteDrive] Database journal_mode is '{JournalMode}' (expected 'wal'). WAL may not be enabled correctly.",
                        journalMode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] Failed to verify journal_mode after applying PRAGMAs");
            }
        }

        private void CreateSchema(IDatabaseConnection conn)
        {
            const string ddl = @"
-- ── catalog_items (JSON-first) ──────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS catalog_items (
    id                      TEXT PRIMARY KEY,
    aio_id                  TEXT NOT NULL,
    tmdb_id                 TEXT,
    title                   TEXT NOT NULL,
    year                    INTEGER,
    media_type              TEXT NOT NULL CHECK(media_type IN ('movie', 'series', 'anime', 'episode', 'other')),
    source                  TEXT NOT NULL,
    source_list_id          TEXT,
    seasons_json            TEXT,
    strm_path               TEXT,
    added_at                TEXT NOT NULL,
    updated_at              TEXT NOT NULL,
    removed_at              TEXT,
    local_path              TEXT,
    local_source            TEXT,
    resurrection_count      INTEGER DEFAULT 0,
    item_state              INTEGER NOT NULL DEFAULT 0,
    pin_source              TEXT,
    pinned_at               TEXT,
    unique_ids_json         TEXT,
    enrichment_status       TEXT,
    retry_count             INTEGER DEFAULT 0,
    next_retry_at           INTEGER,
    blocked_at              TEXT,
    blocked_by              TEXT,
    first_added_by_user_id  TEXT NULL,
    tvdb_id                 TEXT,
    raw_meta_json           TEXT,
    catalog_type            TEXT,
    videos_json             TEXT,
    episodes_expanded       INTEGER,
    last_expanded_at        INTEGER,
    last_verified_at        INTEGER,
    source_manifest_url     TEXT,
    selected_versions_json  TEXT,
    last_version_refresh_at TEXT,
    UNIQUE(aio_id, source)
);
CREATE INDEX IF NOT EXISTS idx_catalog_aio ON catalog_items(aio_id);
CREATE INDEX IF NOT EXISTS idx_catalog_active ON catalog_items(removed_at) WHERE removed_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_catalog_media_type ON catalog_items(media_type, removed_at);

-- ── stream_resolution_cache (consolidated from resolution_cache + stream_candidates + cached_streams) ──
-- aio_id: AIOStreams top-level id (may be IMDB tt-prefix, kitsu:NNNNN, mal:NNNNN, etc.)
-- imdb_id: nullable, only set when a true IMDB ID is known
-- tmdb_key: nullable, TMDB-prefixed compound key for secondary lookup
CREATE TABLE IF NOT EXISTS stream_resolution_cache (
    id                      TEXT PRIMARY KEY,
    aio_id                  TEXT NOT NULL,
    imdb_id                 TEXT,
    tmdb_key                TEXT,
    media_type              TEXT NOT NULL DEFAULT 'movie',
    season                  INTEGER,
    episode                 INTEGER,
    item_id                 TEXT,
    rank                    INTEGER NOT NULL DEFAULT 0,
    provider_key            TEXT NOT NULL DEFAULT 'unknown',
    stream_key              TEXT,
    info_hash               TEXT,
    file_idx                INTEGER,
    url                     TEXT NOT NULL,
    quality_tier            TEXT,
    file_name               TEXT,
    file_size               INTEGER,
    bitrate_kbps            INTEGER,
    languages               TEXT,
    subtitles_json          TEXT,
    description             TEXT,
    binge_group             TEXT,
    headers_json            TEXT,
    raw_stream_json         TEXT,
    probe_json              TEXT,
    variants_json           TEXT DEFAULT '[]',
    manifest_source         TEXT,
    status                  TEXT NOT NULL DEFAULT 'valid',
    is_cached               INTEGER NOT NULL DEFAULT 1,
    url_resolved_at         TEXT,
    url_expires_at          TEXT,
    resolved_at             TEXT NOT NULL,
    expires_at              TEXT NOT NULL,
    updated_at              TEXT,
    play_count              INTEGER NOT NULL DEFAULT 0,
    last_played_at          TEXT,
    retry_count             INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_src_aio ON stream_resolution_cache(aio_id, season, episode);
CREATE INDEX IF NOT EXISTS idx_src_imdb ON stream_resolution_cache(imdb_id);
CREATE INDEX IF NOT EXISTS idx_src_tmdb ON stream_resolution_cache(tmdb_key);
CREATE INDEX IF NOT EXISTS idx_src_expires ON stream_resolution_cache(expires_at);
CREATE INDEX IF NOT EXISTS idx_src_stream_key ON stream_resolution_cache(stream_key);
CREATE INDEX IF NOT EXISTS idx_src_status ON stream_resolution_cache(status);

-- ── playback_log ────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS playback_log (
    id                  TEXT PRIMARY KEY,
    aio_id              TEXT NOT NULL,
    title               TEXT,
    season              INTEGER,
    episode             INTEGER,
    resolution_mode     TEXT NOT NULL CHECK(resolution_mode IN ('cached','fallback_1','fallback_2','sync_resolve','failed')),
    quality_served      TEXT,
    client_type         TEXT,
    latency_ms          INTEGER,
    bitrate_sustained   INTEGER,
    quality_downgrade   INTEGER DEFAULT 0,
    error_message       TEXT,
    played_at           TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_play_recent ON playback_log(played_at DESC);

-- ── api_budget ──────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS api_budget (
    date            TEXT PRIMARY KEY,
    calls_made      INTEGER NOT NULL DEFAULT 0,
    calls_budget    INTEGER NOT NULL DEFAULT 2000,
    last_429_at     TEXT,
    backoff_until   TEXT
);

-- ── sync_state ──────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS sync_state (
    source_key          TEXT PRIMARY KEY,
    last_sync_at        TEXT,
    last_etag           TEXT,
    last_cursor         TEXT,
    item_count          INTEGER DEFAULT 0,
    status              TEXT DEFAULT 'ok',
    consecutive_failures INT DEFAULT 0,
    last_error          TEXT,
    last_reached_at     TEXT,
    catalog_name        TEXT,
    catalog_type        TEXT,
    items_target        INTEGER DEFAULT 0,
    items_running       INTEGER DEFAULT 0,
    updated_at          TEXT
);

-- ── discover_catalog ────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS discover_catalog (
    id                  TEXT PRIMARY KEY,
    aio_id              TEXT NOT NULL,
    title               TEXT NOT NULL,
    year                INTEGER,
    media_type          TEXT NOT NULL CHECK(media_type IN ('movie', 'series')),
    poster_url          TEXT,
    backdrop_url        TEXT,
    overview            TEXT,
    catalog_source      TEXT NOT NULL,
    is_in_user_library  INTEGER NOT NULL DEFAULT 0,
    genres              TEXT,
    imdb_rating         REAL,
    certification       TEXT,
    added_at            TEXT NOT NULL,
    updated_at          TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_discover_aio ON discover_catalog(aio_id);
CREATE INDEX IF NOT EXISTS idx_discover_source ON discover_catalog(catalog_source);
CREATE INDEX IF NOT EXISTS idx_discover_in_library ON discover_catalog(is_in_user_library);
CREATE VIRTUAL TABLE IF NOT EXISTS discover_catalog_fts USING fts5(title, content=discover_catalog, content_rowid=rowid);
CREATE TRIGGER IF NOT EXISTS discover_catalog_fts_insert AFTER INSERT ON discover_catalog BEGIN
  INSERT INTO discover_catalog_fts(rowid, title) VALUES (new.rowid, new.title);
END;
CREATE TRIGGER IF NOT EXISTS discover_catalog_fts_update AFTER UPDATE ON discover_catalog BEGIN
  INSERT INTO discover_catalog_fts(discover_catalog_fts, rowid, title) VALUES('delete', old.rowid, old.title);
  INSERT INTO discover_catalog_fts(rowid, title) VALUES (new.rowid, new.title);
END;
CREATE TRIGGER IF NOT EXISTS discover_catalog_fts_delete AFTER DELETE ON discover_catalog BEGIN
  INSERT INTO discover_catalog_fts(discover_catalog_fts, rowid, title) VALUES('delete', old.rowid, old.title);
END;

-- ── collection_membership ───────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS collection_membership (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    collection_name     TEXT NOT NULL,
    emby_item_id        TEXT NOT NULL,
    source              TEXT NOT NULL,
    last_seen           TEXT NOT NULL,
    UNIQUE(collection_name, emby_item_id)
);

-- ── plugin_metadata ─────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS plugin_metadata (
    key   TEXT PRIMARY KEY NOT NULL,
    value TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

-- ── home_section_tracking ───────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS home_section_tracking (
    id              TEXT PRIMARY KEY,
    user_id         TEXT NOT NULL,
    rail_type       TEXT NOT NULL,
    emby_section_id TEXT,
    section_marker  TEXT NOT NULL,
    created_at      TEXT NOT NULL,
    updated_at      TEXT NOT NULL,
    UNIQUE(user_id, rail_type)
);

-- ── ingestion_state ─────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS ingestion_state (
    source_id      TEXT PRIMARY KEY,
    last_poll_at   TEXT,
    last_found_at  TEXT,
    watermark      TEXT
);

-- ── refresh_run_log ─────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS refresh_run_log (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    run_at          TEXT NOT NULL,
    worker          TEXT NOT NULL,
    step            TEXT NOT NULL,
    status          TEXT NOT NULL DEFAULT 'started',
    items_affected  INTEGER DEFAULT 0,
    notes           TEXT
);

-- ── media_items ─────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS media_items (
    id                  TEXT PRIMARY KEY,
    primary_id_type     TEXT NOT NULL,
    primary_id          TEXT NOT NULL,
    media_type          TEXT NOT NULL CHECK (media_type IN ('movie','series')),
    title               TEXT NOT NULL,
    year                INTEGER,
    status              TEXT NOT NULL CHECK (status IN ('known','resolved','hydrated','created','indexed','active','failed','deleted')),
    failure_reason      TEXT CHECK (failure_reason IN ('none','no_streams_found','metadata_fetch_failed','file_write_error','emby_index_timeout','digital_release_gate','blocked')),
    saved               INTEGER NOT NULL DEFAULT 0,
    saved_at            TEXT,
    blocked             INTEGER NOT NULL DEFAULT 0,
    blocked_at          TEXT,
    created_at          TEXT NOT NULL,
    updated_at          TEXT NOT NULL,
    grace_started_at    TEXT,
    superseded          INTEGER NOT NULL DEFAULT 0,
    superseded_conflict INTEGER NOT NULL DEFAULT 0,
    superseded_at       TEXT,
    emby_item_id        TEXT,
    emby_indexed_at     TEXT,
    strm_path           TEXT,
    watch_progress_pct  INTEGER NOT NULL DEFAULT 0,
    favorited           INTEGER NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_media_items_primary ON media_items(primary_id_type, primary_id);
CREATE INDEX IF NOT EXISTS idx_media_items_status ON media_items(status);
CREATE INDEX IF NOT EXISTS idx_media_items_saved ON media_items(saved);
CREATE INDEX IF NOT EXISTS idx_media_items_blocked ON media_items(blocked);
CREATE INDEX IF NOT EXISTS idx_media_items_emby_id ON media_items(emby_item_id) WHERE emby_item_id IS NOT NULL;

-- ── media_item_ids ──────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS media_item_ids (
    id          TEXT PRIMARY KEY,
    media_item_id TEXT NOT NULL,
    id_type     TEXT NOT NULL CHECK (id_type IN ('tmdb','imdb','tvdb','anilist','anidb','kitsu')),
    id_value    TEXT NOT NULL,
    is_primary  INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT NOT NULL,
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_media_item_ids_item ON media_item_ids(media_item_id);
CREATE INDEX IF NOT EXISTS idx_media_item_ids_type_value ON media_item_ids(id_type, id_value);
CREATE UNIQUE INDEX IF NOT EXISTS idx_media_item_ids_unique ON media_item_ids(media_item_id, id_type, id_value);

-- ── source_memberships ──────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS source_memberships (
    id              TEXT PRIMARY KEY,
    source_id       TEXT NOT NULL,
    media_item_id   TEXT NOT NULL,
    user_catalog_id TEXT,
    created_at      TEXT NOT NULL,
    FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE CASCADE,
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    UNIQUE (source_id, media_item_id)
);
CREATE INDEX IF NOT EXISTS idx_source_memberships_source ON source_memberships(source_id);
CREATE INDEX IF NOT EXISTS idx_source_memberships_item ON source_memberships(media_item_id);
CREATE INDEX IF NOT EXISTS idx_source_memberships_user_catalog ON source_memberships(user_catalog_id);

-- ── sources ─────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS sources (
    id                  TEXT PRIMARY KEY,
    name                TEXT NOT NULL,
    url                 TEXT,
    type                TEXT NOT NULL CHECK(type IN ('BuiltIn', 'Aio', 'UserRss')),
    enabled             INTEGER NOT NULL DEFAULT 1,
    show_as_collection  INTEGER NOT NULL DEFAULT 0,
    max_items           INTEGER NOT NULL DEFAULT 100,
    sync_interval_hours INTEGER NOT NULL DEFAULT 6,
    last_synced_at      TEXT,
    emby_collection_id  TEXT,
    collection_name     TEXT,
    created_at          TEXT NOT NULL,
    updated_at          TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sources_enabled ON sources(enabled);

-- ── collections ─────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS collections (
    id                  TEXT PRIMARY KEY,
    source_id           TEXT NOT NULL,
    name                TEXT NOT NULL,
    emby_collection_id  TEXT,
    collection_name     TEXT,
    last_synced_at      TEXT,
    created_at          TEXT NOT NULL,
    updated_at          TEXT NOT NULL,
    FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_collections_source_id ON collections(source_id);

-- ── item_pipeline_log ──────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS item_pipeline_log (
    primary_id       TEXT NOT NULL,
    primary_id_type  TEXT NOT NULL,
    media_type       TEXT NOT NULL,
    phase            TEXT NOT NULL,
    trigger          TEXT NOT NULL,
    success          INTEGER NOT NULL,
    details          TEXT,
    timestamp        TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_pipeline_timestamp ON item_pipeline_log(timestamp);
CREATE INDEX IF NOT EXISTS idx_pipeline_primary_id ON item_pipeline_log(primary_id);

-- ── stream_resolution_log ──────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS stream_resolution_log (
    primary_id       TEXT NOT NULL,
    primary_id_type  TEXT NOT NULL,
    media_type       TEXT NOT NULL,
    media_id         TEXT NOT NULL,
    stream_count     INTEGER NOT NULL,
    selected_stream  TEXT,
    duration_ms      INTEGER NOT NULL,
    timestamp        TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_resolution_timestamp ON stream_resolution_log(timestamp);
CREATE INDEX IF NOT EXISTS idx_resolution_primary_id ON stream_resolution_log(primary_id);

-- ── user_catalogs ───────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS user_catalogs (
    id                 TEXT PRIMARY KEY,
    owner_user_id      TEXT NOT NULL,
    source_type        TEXT NOT NULL DEFAULT 'external_list',
    service            TEXT NOT NULL,
    list_url           TEXT NOT NULL,
    display_name       TEXT NOT NULL,
    active             INTEGER NOT NULL DEFAULT 1,
    last_synced_at     TEXT,
    last_sync_status   TEXT,
    created_at         TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_user_catalogs_owner ON user_catalogs(owner_user_id);
CREATE INDEX IF NOT EXISTS idx_user_catalogs_active ON user_catalogs(active);

-- ── user_item_saves ─────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS user_item_saves (
    id            TEXT PRIMARY KEY,
    user_id       TEXT NOT NULL,
    media_item_id TEXT NOT NULL,
    save_reason   TEXT CHECK (save_reason IN ('explicit','watched_episode','admin_override')),
    saved_season  INTEGER,
    saved_at      TEXT NOT NULL,
    FOREIGN KEY (media_item_id) REFERENCES media_items(id) ON DELETE CASCADE,
    UNIQUE (user_id, media_item_id)
);
CREATE INDEX IF NOT EXISTS idx_user_saves_user ON user_item_saves(user_id);
CREATE INDEX IF NOT EXISTS idx_user_saves_item ON user_item_saves(media_item_id);

-- ── blocked_items ──────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS blocked_items (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    aio_id       TEXT,
    tmdb_id      TEXT,
    anilist_id   TEXT,
    title        TEXT NOT NULL,
    media_type   TEXT NOT NULL,
    blocked_at   TEXT NOT NULL,
    blocked_by   TEXT NOT NULL,
    unblocked_at TEXT,
    unblocked_by TEXT
);
CREATE INDEX IF NOT EXISTS idx_bi_aio ON blocked_items(lower(aio_id));
CREATE INDEX IF NOT EXISTS idx_bi_tmdb ON blocked_items(lower(tmdb_id));
CREATE INDEX IF NOT EXISTS idx_bi_anilist ON blocked_items(lower(anilist_id));
";
            // Split on semicolons but preserve BEGIN...END blocks in triggers.
            var statements = SplitDdl(ddl);
            foreach (var statement in statements)
            {
                var sql = statement.Trim();
                if (!string.IsNullOrEmpty(sql))
                    conn.Execute(sql);
            }

            _logger.LogInformation("[InfiniteDrive] Schema created successfully");
        }

        // ── Private: integrity check ────────────────────────────────────────────

        private bool TryIntegrityCheck()
        {
            if (!File.Exists(_dbPath))
                return true; // Fresh install — nothing to check

            try
            {
                using var conn = OpenConnection();
                using var stmt = conn.PrepareStatement("PRAGMA integrity_check;");
                foreach (var row in stmt.AsRows())
                    return string.Equals(row.GetString(0), "ok", StringComparison.OrdinalIgnoreCase);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InfiniteDrive] integrity_check threw — treating as corrupt");
                return false;
            }
        }

        // ── Private: SQLitePCL.pretty helpers ───────────────────────────────────

        private IDatabaseConnection OpenConnection()
        {
            var conn = SQLite3.Open(_dbPath, ConnectionFlags.ReadWrite | ConnectionFlags.Create, null, true);
            // Set busy_timeout on every connection so SQLite waits for locks instead of
            // immediately failing. This prevents "database is locked" errors when
            // SQLitePCL.pretty runs PRAGMA optimize on dispose while a write is active.
            conn.Execute("PRAGMA busy_timeout=30000;");
            return conn;
        }

        private Task ExecuteNonQueryAsync(string sql, Action<IStatement> bindParams)
        {
            using var conn = OpenConnection();
            conn.RunInTransaction(c =>
            {
                using var stmt = c.PrepareStatement(sql);
                bindParams(stmt);
                while (stmt.MoveNext()) { }
            });
            return Task.CompletedTask;
        }

        /// <summary>
        /// Executes a write operation with serialization through the write gate.
        /// This prevents "database is locked" errors when multiple threads/services
        /// try to write concurrently (e.g., CatalogSyncTask and CatalogDiscoverService).
        /// </summary>
        private async Task ExecuteWriteAsync(string sql, Action<IStatement> bindParams, CancellationToken cancellationToken = default)
        {
            await _dbWriteGate.WaitAsync(cancellationToken);
            try
            {
                using var conn = OpenConnection();
                conn.RunInTransaction(c =>
                {
                    using var stmt = c.PrepareStatement(sql);
                    bindParams(stmt);
                    while (stmt.MoveNext()) { }
                });
            }
            finally
            {
                _dbWriteGate.Release();
            }
        }

        private Task<T?> QuerySingleAsync<T>(
            string sql,
            Action<IStatement>? bindParams,
            Func<IResultSet, T> map) where T : class
        {
            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            bindParams?.Invoke(stmt);
            foreach (var row in stmt.AsRows())
                return Task.FromResult<T?>(map(row));
            return Task.FromResult<T?>(null);
        }

        internal Task<List<T>> QueryListAsync<T>(
            string sql,
            Action<IStatement>? bindParams,
            Func<IResultSet, T> map)
        {
            using var conn = OpenConnection();
            var results = new List<T>();
            using var stmt = conn.PrepareStatement(sql);
            bindParams?.Invoke(stmt);
            foreach (var row in stmt.AsRows())
                results.Add(map(row));
            return Task.FromResult(results);
        }

        /// <summary>
        /// Split DDL into individual statements, respecting BEGIN...END blocks
        /// in triggers so that internal semicolons are not treated as statement boundaries.
        /// </summary>
        private static List<string> SplitDdl(string ddl)
        {
            var result = new List<string>();
            var current = new System.Text.StringBuilder();
            bool insideTrigger = false;

            foreach (var line in ddl.Split('\n'))
            {
                var trimmed = line.Trim();

                // Skip empty lines and comments
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("--"))
                {
                    continue;
                }

                // Detect trigger body entry: line contains CREATE TRIGGER ... BEGIN
                if (!insideTrigger &&
                    trimmed.StartsWith("CREATE TRIGGER", StringComparison.OrdinalIgnoreCase) &&
                    trimmed.Contains("BEGIN", StringComparison.OrdinalIgnoreCase))
                {
                    insideTrigger = true;
                }

                // Detect trigger body exit: line is exactly END or END;
                if (insideTrigger &&
                    (trimmed.Equals("END", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Equals("END;", StringComparison.OrdinalIgnoreCase)))
                {
                    insideTrigger = false;
                    current.AppendLine(line);
                    result.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.AppendLine(line);

                // Statement boundary: semicolon at end of line AND not inside trigger
                if (!insideTrigger && trimmed.EndsWith(";"))
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }

            // Remaining content
            if (current.Length > 0)
            {
                var remainder = current.ToString().Trim();
                if (!string.IsNullOrEmpty(remainder))
                    result.Add(remainder);
            }

            return result;
        }

        private static void ExecuteInline(IDatabaseConnection conn, string sql)
            => conn.Execute(sql);

        // ── Private: parameter binders ──────────────────────────────────────────

        private static void BindText(IStatement stmt, string name, string value)
            => stmt.BindParameters[name].Bind(value);

        private static void BindNullableText(IStatement stmt, string name, string? value)
        {
            if (value == null) stmt.BindParameters[name].BindNull();
            else               stmt.BindParameters[name].Bind(value);
        }

        private static void BindNullableInt(IStatement stmt, string name, int? value)
        {
            if (value == null) stmt.BindParameters[name].BindNull();
            else               stmt.BindParameters[name].Bind(value.Value);
        }

        private static void BindInt(IStatement stmt, string name, int value)
            => stmt.BindParameters[name].Bind(value);

        private static void BindNullableLong(IStatement stmt, string name, long? value)
        {
            if (value == null) stmt.BindParameters[name].BindNull();
            else               stmt.BindParameters[name].Bind(value.Value);
        }

        private static long GetLastInsertRowId(IDatabaseConnection conn)
        {
            using var stmt = conn.PrepareStatement("SELECT last_insert_rowid();");
            foreach (var row in stmt.AsRows())
                return row.GetInt64(0);
            return -1;
        }

        // Discover + Channel methods moved to DatabaseManager.Discover.cs


        // ── Private: column maps + row mappers ──────────────────────────────────

        /// <summary>
        /// Populates column-name-to-index maps for all tables via PRAGMA table_info.
        /// Called once during Initialise(). SELECT * returns columns in this order.
        /// </summary>
        private static void BuildColumnMaps(IDatabaseConnection conn)
        {
            foreach (var table in new[]
            {
                Tables.CatalogItems, Tables.StreamResolutionCache,
                Tables.PlaybackLog, Tables.SyncState,
                "discover_catalog", "user_catalogs",
                "media_items", "sources", "collection_membership"
            })
            {
                try
                {
                    var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    using var stmt = conn.PrepareStatement($"PRAGMA table_info('{table}');");
                    foreach (var row in stmt.AsRows())
                    {
                        // PRAGMA table_info columns: 0=cid, 1=name, 2=type, 3=notnull, 4=dflt, 5=pk
                        var name = row.GetString(1);
                        var cid = row.GetInt(0);
                        map[name] = cid;
                    }
                    if (map.Count > 0)
                        _columnMaps[table] = map;
                }
                catch
                {
                    // Table may not exist yet — skip
                }
            }
        }

        private static Dictionary<string, int> ColMap(string table)
            => _columnMaps.TryGetValue(table, out var m) ? m
               : throw new InvalidOperationException($"No column map for table '{table}'. Call BuildColumnMaps first.");

        private static string? GetStr(Dictionary<string, int> map, IResultSet r, string col)
            => map.TryGetValue(col, out var i) && !r.IsDBNull(i) ? r.GetString(i) : null;

        private static string GetReqStr(Dictionary<string, int> map, IResultSet r, string col)
            => r.GetString(map[col]);

        private static int? GetInt(Dictionary<string, int> map, IResultSet r, string col)
            => map.TryGetValue(col, out var i) && !r.IsDBNull(i) ? r.GetInt(i) : null;

        private static int GetReqInt(Dictionary<string, int> map, IResultSet r, string col)
            => r.GetInt(map[col]);

        private static long? GetLong(Dictionary<string, int> map, IResultSet r, string col)
            => map.TryGetValue(col, out var i) && !r.IsDBNull(i) ? r.GetInt64(i) : null;

        private static bool? GetBool(Dictionary<string, int> map, IResultSet r, string col)
            => map.TryGetValue(col, out var i) && !r.IsDBNull(i) ? r.GetInt(i) == 1 : null;

        private static double? GetDouble(Dictionary<string, int> map, IResultSet r, string col)
            => map.TryGetValue(col, out var i) && !r.IsDBNull(i) ? r.GetDouble(i) : null;

        private static CatalogItem ReadCatalogItem(IResultSet r)
        {
            var m = ColMap(Tables.CatalogItems);
            return new CatalogItem
            {
                Id                = GetReqStr(m, r, "id"),
                AioId             = GetReqStr(m, r, "aio_id"),
                TmdbId            = GetStr(m, r, "tmdb_id"),
                Title             = GetReqStr(m, r, "title"),
                Year              = GetInt(m, r, "year"),
                MediaType         = GetReqStr(m, r, "media_type"),
                Source            = GetReqStr(m, r, "source"),
                SourceListId      = GetStr(m, r, "source_list_id"),
                SeasonsJson       = GetStr(m, r, "seasons_json"),
                StrmPath          = GetStr(m, r, "strm_path"),
                AddedAt           = GetReqStr(m, r, "added_at"),
                UpdatedAt         = GetReqStr(m, r, "updated_at"),
                RemovedAt         = GetStr(m, r, "removed_at"),
                LocalPath         = GetStr(m, r, "local_path"),
                LocalSource       = GetStr(m, r, "local_source"),
                ResurrectionCount = GetInt(m, r, "resurrection_count") ?? 0,
                ItemState         = GetInt(m, r, "item_state") is int s ? (ItemState)s : ItemState.Catalogued,
                PinSource         = GetStr(m, r, "pin_source"),
                PinnedAt          = GetStr(m, r, "pinned_at"),
                UniqueIdsJson     = GetStr(m, r, "unique_ids_json"),
                EnrichmentStatus  = GetStr(m, r, "enrichment_status"),
                RetryCount        = GetInt(m, r, "retry_count") ?? 0,
                NextRetryAt       = GetLong(m, r, "next_retry_at"),
                BlockedAt         = GetStr(m, r, "blocked_at"),
                BlockedBy         = GetStr(m, r, "blocked_by"),
                FirstAddedByUserId = GetStr(m, r, "first_added_by_user_id"),
                TvdbId            = GetStr(m, r, "tvdb_id"),
                RawMetaJson       = GetStr(m, r, "raw_meta_json"),
                CatalogType       = GetStr(m, r, "catalog_type"),
                VideosJson        = GetStr(m, r, "videos_json"),
                EpisodesExpanded  = GetBool(m, r, "episodes_expanded"),
                LastExpandedAt    = GetLong(m, r, "last_expanded_at"),
                LastVerifiedAt    = GetLong(m, r, "last_verified_at"),
                SourceManifestUrl = GetStr(m, r, "source_manifest_url"),
                SelectedVersionsJson = GetStr(m, r, "selected_versions_json"),
                LastVersionRefreshAt = GetStr(m, r, "last_version_refresh_at"),
            };
        }

        // These mappers use queries with explicit column lists — positional is safe.
        // Name-based lookup is only used with SELECT * queries (ReadCatalogItem).

        // Maps stream_resolution_cache columns to ResolutionEntry (rank-0 queries)
        private static ResolutionEntry ReadResolutionEntryFromCache(IResultSet r) => new ResolutionEntry
        {
            Id              = r.IsDBNull(0)  ? "" : r.GetString(0),
            AioId           = r.GetString(1),
            Season          = r.IsDBNull(2)  ? null : r.GetInt(2),
            Episode         = r.IsDBNull(3)  ? null : r.GetInt(3),
            StreamUrl       = r.IsDBNull(4)  ? "" : r.GetString(4),
            QualityTier     = r.IsDBNull(5)  ? null : r.GetString(5),
            FileName        = r.IsDBNull(6)  ? null : r.GetString(6),
            FileSize        = r.IsDBNull(7)  ? null : r.GetInt64(7),
            FileBitrateKbps = r.IsDBNull(8)  ? null : r.GetInt(8),
            TorrentHash     = r.IsDBNull(9)  ? null : r.GetString(9),
            Status          = r.IsDBNull(11) ? "" : r.GetString(11),
            ResolvedAt      = r.IsDBNull(12) ? "" : r.GetString(12),
            ExpiresAt       = r.IsDBNull(13) ? "" : r.GetString(13),
            PlayCount       = r.IsDBNull(14) ? 0 : r.GetInt(14),
            LastPlayedAt    = r.IsDBNull(15) ? null : r.GetString(15),
            RetryCount      = r.IsDBNull(16) ? 0 : r.GetInt(16),
        };

        private static StreamCandidate ReadStreamCandidate(IResultSet r) => new StreamCandidate
        {
            Id          = r.GetString(0),
            AioId       = r.GetString(1),
            Season      = r.IsDBNull(2)  ? null : r.GetInt(2),
            Episode     = r.IsDBNull(3)  ? null : r.GetInt(3),
            Rank        = r.GetInt(4),
            ProviderKey = r.GetString(5),
            StreamType  = r.GetString(6),
            Url         = r.GetString(7),
            HeadersJson = r.IsDBNull(8)  ? null : r.GetString(8),
            QualityTier = r.IsDBNull(9)  ? null : r.GetString(9),
            FileName    = r.IsDBNull(10) ? null : r.GetString(10),
            FileSize    = r.IsDBNull(11) ? null : r.GetInt64(11),
            BitrateKbps = r.IsDBNull(12) ? null : r.GetInt(12),
            IsCached    = r.GetInt(13) != 0,
            ResolvedAt  = r.GetString(14),
            ExpiresAt   = r.GetString(15),
            Status      = r.GetString(16),
            InfoHash    = r.IsDBNull(17) ? null : r.GetString(17),
            FileIdx     = r.IsDBNull(18) ? null : r.GetInt(18),
            StreamKey   = r.IsDBNull(19) ? null : r.GetString(19),
            BingeGroup  = r.IsDBNull(20) ? null : r.GetString(20),
            Languages     = r.IsDBNull(21) ? null : r.GetString(21),
            SubtitlesJson = r.IsDBNull(22) ? null : r.GetString(22),
            ProbeJson     = r.IsDBNull(23) ? null : r.GetString(23),
            Description   = r.IsDBNull(24) ? null : r.GetString(24),
            RawStreamJson = r.IsDBNull(25) ? null : r.GetString(25),
        };

        private static PlaybackEntry ReadPlaybackEntry(IResultSet r) => new PlaybackEntry
        {
            Id             = r.GetString(0),
            AioId          = r.GetString(1),
            Title          = r.IsDBNull(2)  ? null : r.GetString(2),
            Season         = r.IsDBNull(3)  ? null : r.GetInt(3),
            Episode        = r.IsDBNull(4)  ? null : r.GetInt(4),
            ResolutionMode = r.GetString(5),
            QualityServed  = r.IsDBNull(6)  ? null : r.GetString(6),
            ClientType     = r.IsDBNull(7)  ? null : r.GetString(7),
            LatencyMs      = r.IsDBNull(8)  ? null : r.GetInt(8),
            BitrateSustained = r.IsDBNull(9) ? null : r.GetInt(9),
            QualityDowngrade = r.GetInt(10),
            ErrorMessage   = r.IsDBNull(11) ? null : r.GetString(11),
            PlayedAt       = r.GetString(12),
        };

        private static SyncState ReadSyncState(IResultSet r) => new SyncState
        {
            SourceKey           = r.GetString(0),
            LastSyncAt          = r.IsDBNull(1)  ? null : r.GetString(1),
            LastEtag            = r.IsDBNull(2)  ? null : r.GetString(2),
            LastCursor          = r.IsDBNull(3)  ? null : r.GetString(3),
            ItemCount           = r.GetInt(4),
            Status              = r.GetString(5),
            ConsecutiveFailures = r.IsDBNull(6)  ? 0    : r.GetInt(6),
            LastError           = r.IsDBNull(7)  ? null : r.GetString(7),
            LastReachedAt       = r.IsDBNull(8)  ? null : r.GetString(8),
            CatalogName         = r.IsDBNull(9)  ? null : r.GetString(9),
            CatalogType         = r.IsDBNull(10) ? null : r.GetString(10),
            ItemsTarget         = r.IsDBNull(11) ? 0    : r.GetInt(11),
            ItemsRunning        = r.IsDBNull(12) ? 0    : r.GetInt(12),
        };

        // discover_catalog queries use explicit column lists — positional mapping is safe
        private static DiscoverCatalogEntry ReadDiscoverCatalogEntry(IResultSet r) => new DiscoverCatalogEntry
        {
            Id              = r.GetString(0),
            AioId           = r.GetString(1),
            Title           = r.GetString(2),
            Year            = r.IsDBNull(3)  ? null : r.GetInt(3),
            MediaType       = r.GetString(4),
            PosterUrl       = r.IsDBNull(5)  ? null : r.GetString(5),
            BackdropUrl     = r.IsDBNull(6)  ? null : r.GetString(6),
            Overview        = r.IsDBNull(7)  ? null : r.GetString(7),
            Genres          = r.IsDBNull(8)  ? null : r.GetString(8),
            ImdbRating      = r.IsDBNull(9)  ? null : r.GetDouble(9),
            Certification   = r.IsDBNull(10) ? null : r.GetString(10),
            CatalogSource   = r.GetString(11),
            IsInUserLibrary = r.GetInt(12) != 0,
            AddedAt         = r.GetString(13),
        };

        #region IResolutionCacheRepository Explicit Implementation

        async Task<string?> IResolutionCacheRepository.GetCachedUrlAsync(
            string aioId, int? season, int episode, CancellationToken ct)
        {
            var entry = await GetCachedStreamAsync(aioId, season, episode);
            return entry?.StreamUrl;
        }

        async Task IResolutionCacheRepository.SetCachedUrlAsync(
            string aioId, int? season, int episode,
            string resolvedUrl, DateTime expiresAt, CancellationToken ct)
        {
            const string sql = @"
                UPDATE stream_resolution_cache
                SET url = @url, expires_at = @expires_at, status = 'valid',
                    resolved_at = datetime('now'), updated_at = datetime('now')
                WHERE aio_id = @aio_id
                  AND rank = 0
                  AND (season IS @season OR (season IS NULL AND @season IS NULL))
                  AND episode = @episode;";
            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@aio_id", aioId);
                BindNullableInt(cmd, "@season", season);
                BindNullableInt(cmd, "@episode", episode == 0 ? null : (int?)episode);
                BindText(cmd, "@url", resolvedUrl);
                BindText(cmd, "@expires_at", expiresAt.ToString("o"));
            }, ct);
        }

        async Task IResolutionCacheRepository.InvalidateAsync(string aioId, CancellationToken ct)
        {
            const string sql = @"
                UPDATE stream_resolution_cache
                SET status = 'stale', updated_at = datetime('now')
                WHERE aio_id = @aio_id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@aio_id", aioId);
            });
        }

        async Task IResolutionCacheRepository.PurgeExpiredAsync(CancellationToken ct)
        {
            const string sql = @"
                DELETE FROM stream_resolution_cache
                WHERE expires_at < datetime('now');";

            await ExecuteWriteAsync(sql, _ => { });
        }

        #endregion

        // ── Media Items, Sources, User Saves, Logs → DatabaseManager.MediaItems.cs
        // ── Media Item Ops, User Catalogs, Readers, Blocked → DatabaseManager.Catalog.cs
    }
    /// Extension methods to bridge <see cref="IStatement"/> (IEnumerator) to
    /// a LINQ-compatible <see cref="IEnumerable{IResultSet}"/> for foreach usage.
    /// <c>IStatement</c> implements <c>IEnumerator&lt;IResultSet&gt;</c>; wrapping
    /// it lets us keep the familiar <c>foreach (var row in stmt.AsRows())</c> pattern.
    /// </summary>
    internal static class StatementExtensions
    {
        internal static System.Collections.Generic.IEnumerable<IResultSet> AsRows(
            this IStatement stmt)
        {
            while (stmt.MoveNext())
                yield return stmt.Current;
        }
    }
}