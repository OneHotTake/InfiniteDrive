using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Models;
using SQLitePCL.pretty;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Data
{
    public partial class DatabaseManager
    {
        // ── Media Item Operations ───────────────────────────────────────────────────────

        /// <summary>
        /// Sets the superseded flag for a media item.
        /// </summary>
        public async Task SetSupersededAsync(string itemId, bool superseded, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE media_items
                SET superseded = @Superseded,
                    updated_at = datetime('now')
                WHERE id = @ItemId;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@ItemId", itemId);
                cmd.BindParameters["@Superseded"].Bind(superseded ? 1 : 0);
            }, cancellationToken);
        }

        /// <summary>
        /// Sets the superseded_conflict flag for a media item.
        /// </summary>
        public async Task SetSupersededConflictAsync(string itemId, bool supersededConflict, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE media_items
                SET superseded_conflict = @SupersededConflict,
                    updated_at = datetime('now')
                WHERE id = @ItemId;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@ItemId", itemId);
                cmd.BindParameters["@SupersededConflict"].Bind(supersededConflict ? 1 : 0);
            }, cancellationToken);
        }

        /// <summary>
        /// Sets the superseded_at timestamp for a media item.
        /// </summary>
        public async Task SetSupersededAtAsync(string itemId, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE media_items
                SET superseded_at = @SupersededAt,
                    updated_at = datetime('now')
                WHERE id = @ItemId;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@ItemId", itemId);
                BindText(cmd, "@SupersededAt", timestamp.ToString("o"));
            }, cancellationToken);
        }

        // ── user_catalogs CRUD (Sprint 158) ─────────────────────────────────────

        /// <summary>
        /// Inserts a new user catalog row. Returns the generated UUID.
        /// </summary>
        public async Task<string> CreateUserCatalogAsync(
            string ownerUserId,
            string service,
            string listUrl,
            string displayName,
            CancellationToken ct = default)
        {
            var id = Guid.NewGuid().ToString();
            const string sql = @"
                INSERT INTO user_catalogs
                    (id, owner_user_id, source_type, service, list_url, display_name, active, created_at)
                VALUES
                    (@id, @owner_user_id, 'external_list', @service, @list_url, @display_name, 1, @created_at);";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id", id);
                BindText(cmd, "@owner_user_id", ownerUserId);
                BindText(cmd, "@service", service);
                BindText(cmd, "@list_url", listUrl);
                BindText(cmd, "@display_name", displayName);
                BindText(cmd, "@created_at", DateTimeOffset.UtcNow.ToString("o"));
            }, ct);

            return id;
        }

        /// <summary>
        /// Returns user catalogs for the given owner. Pass activeOnly=true to filter inactive rows.
        /// </summary>
        public async Task<IReadOnlyList<Models.UserCatalog>> GetUserCatalogsByOwnerAsync(
            string ownerUserId,
            bool activeOnly,
            CancellationToken ct = default)
        {
            var sql = activeOnly
                ? "SELECT id, owner_user_id, source_type, service, list_url, display_name, active, last_synced_at, last_sync_status, created_at FROM user_catalogs WHERE owner_user_id = @owner_user_id AND active = 1 ORDER BY created_at;"
                : "SELECT id, owner_user_id, source_type, service, list_url, display_name, active, last_synced_at, last_sync_status, created_at FROM user_catalogs WHERE owner_user_id = @owner_user_id ORDER BY created_at;";

            return await QueryListAsync(sql,
                cmd => BindText(cmd, "@owner_user_id", ownerUserId),
                ReadUserCatalog).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns all active user catalogs across all users (used by CatalogSyncTask).
        /// </summary>
        public async Task<IReadOnlyList<Models.UserCatalog>> GetAllActiveUserCatalogsAsync(CancellationToken ct = default)
        {
            const string sql = @"
                SELECT id, owner_user_id, source_type, service, list_url, display_name, active,
                       last_synced_at, last_sync_status, created_at
                FROM user_catalogs WHERE active = 1 ORDER BY created_at;";

            return await QueryListAsync(sql, null, ReadUserCatalog).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns a single user catalog by ID, or null if not found.
        /// </summary>
        public Task<Models.UserCatalog?> GetUserCatalogByIdAsync(string catalogId, CancellationToken ct = default)
        {
            const string sql = @"
                SELECT id, owner_user_id, source_type, service, list_url, display_name, active,
                       last_synced_at, last_sync_status, created_at
                FROM user_catalogs WHERE id = @id;";

            return QuerySingleAsync(sql,
                cmd => BindText(cmd, "@id", catalogId),
                ReadUserCatalog);
        }

        /// <summary>
        /// Returns total count of active user catalogs across all non-SERVER owners.
        /// Used by the admin Lists tab to warn when reducing the per-user limit.
        /// </summary>
        public async Task<int> GetActiveUserCatalogCountAsync()
        {
            const string sql = "SELECT COUNT(*) FROM user_catalogs WHERE active = 1 AND owner_user_id <> 'SERVER';";
            return await QueryScalarIntAsync(sql);
        }

        private static Models.UserCatalog ReadUserCatalog(IResultSet row) =>
            new Models.UserCatalog
            {
                Id             = row.GetString(0),
                OwnerUserId    = row.GetString(1),
                SourceType     = row.GetString(2),
                Service        = row.GetString(3),
                ListUrl        = row.GetString(4),
                DisplayName    = row.GetString(5),
                Active         = row.GetInt(6) == 1,
                LastSyncedAt   = row.IsDBNull(7) ? null : row.GetString(7),
                LastSyncStatus = row.IsDBNull(8) ? null : row.GetString(8),
                CreatedAt      = row.GetString(9),
            };

        /// <summary>
        /// Sets the active flag on a user catalog (soft-delete or restore).
        /// </summary>
        public async Task SetUserCatalogActiveAsync(string catalogId, bool active, CancellationToken ct = default)
        {
            const string sql = "UPDATE user_catalogs SET active = @active WHERE id = @id;";
            await ExecuteWriteAsync(sql, cmd =>
            {
                BindInt(cmd, "@active", active ? 1 : 0);
                BindText(cmd, "@id", catalogId);
            }, ct);
        }

        /// <summary>
        /// Updates last_synced_at and last_sync_status after a sync run.
        /// </summary>
        public async Task UpdateUserCatalogSyncStatusAsync(
            string catalogId,
            DateTimeOffset syncedAt,
            string status,
            CancellationToken ct = default)
        {
            const string sql = @"
                UPDATE user_catalogs
                SET last_synced_at = @synced_at, last_sync_status = @status
                WHERE id = @id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@synced_at", syncedAt.ToString("o"));
                BindText(cmd, "@status", status);
                BindText(cmd, "@id", catalogId);
            }, ct);
        }

        /// <summary>
        /// Upserts a source membership, optionally linking it to a user catalog.
        /// Pass null for userCatalogId for system-catalog memberships.
        /// </summary>
        public async Task UpsertSourceMembershipWithCatalogAsync(
            string sourceId,
            string mediaItemId,
            string? userCatalogId,
            CancellationToken ct = default)
        {
            const string sql = @"
                INSERT INTO source_memberships (source_id, media_item_id, user_catalog_id)
                VALUES (@SourceId, @MediaItemId, @UserCatalogId)
                ON CONFLICT(source_id, media_item_id) DO UPDATE SET
                    user_catalog_id = COALESCE(excluded.user_catalog_id, source_memberships.user_catalog_id);";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@SourceId", sourceId);
                BindText(cmd, "@MediaItemId", mediaItemId);
                BindNullableText(cmd, "@UserCatalogId", userCatalogId);
            }, ct);
        }

        /// <summary>
        /// Gets all collection as entities.
        /// </summary>
        public async Task<List<Models.Collection>> GetAllCollectionsListAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, name, emby_collection_id, source_id,
                       enabled, collection_name, last_synced_at, created_at, updated_at
                FROM collections
                ORDER BY name;";

            return await QueryListAsync(sql, null, ReadCollection);
        }

        /// <summary>
        /// Gets a collection by source ID.
        /// </summary>
        public async Task<Models.Collection?> GetCollectionBySourceIdAsync(string sourceId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, name, emby_collection_id, source_id,
                       enabled, collection_name, last_synced_at, created_at, updated_at
                FROM collections
                WHERE source_id = @SourceId
                LIMIT 1;";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@SourceId", sourceId),
                ReadCollection);
        }

        /// <summary>
        /// Upserts a collection.
        /// </summary>
        public async Task UpsertCollectionAsync(Models.Collection collection, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO collections (id, name, emby_collection_id, source_id, enabled, collection_name, last_synced_at, created_at, updated_at)
                VALUES (@Id, @Name, @EmbyCollectionId, @SourceId, @Enabled, @CollectionName, @LastSyncedAt, @CreatedAt, @UpdatedAt)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    emby_collection_id = excluded.emby_collection_id,
                    source_id = excluded.source_id,
                    enabled = excluded.enabled,
                    collection_name = excluded.collection_name,
                    last_synced_at = excluded.last_synced_at,
                    updated_at = excluded.updated_at;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@Id", collection.Id);
                BindText(cmd, "@Name", collection.Name);
                BindNullableText(cmd, "@EmbyCollectionId", collection.EmbyCollectionId);
                BindNullableText(cmd, "@SourceId", collection.SourceId);
                cmd.BindParameters["@Enabled"].Bind(collection.Enabled ? 1 : 0);
                BindNullableText(cmd, "@CollectionName", collection.CollectionName);
                BindNullableText(cmd, "@LastSyncedAt", collection.LastSyncedAt?.ToString("o"));
                BindText(cmd, "@CreatedAt", collection.CreatedAt.ToString("o"));
                BindText(cmd, "@UpdatedAt", collection.UpdatedAt.ToString("o"));
            }, cancellationToken);
        }

        /// <summary>
        /// Deletes a collection by ID.
        /// </summary>
        public async Task DeleteCollectionAsync(string collectionId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                DELETE FROM collections
                WHERE id = @Id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@Id", collectionId);
            }, cancellationToken);
        }

        /// <summary>
        /// Logs a pipeline event.
        /// </summary>
        public async Task LogPipelineEventAsync(
            string primaryId,
            string primaryIdType,
            string mediaType,
            string triggerType,
            string? fromStatus,
            string? toStatus,
            bool success,
            string? errorMessage = null,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO item_pipeline_log
                    (primary_id, primary_id_type, media_type, trigger_type, from_status, to_status, result, error_message)
                VALUES
                    (@PrimaryId, @PrimaryIdType, @MediaType, @TriggerType, @FromStatus, @ToStatus, @Result, @ErrorMessage);";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@PrimaryId", primaryId);
                BindText(cmd, "@PrimaryIdType", primaryIdType);
                BindText(cmd, "@MediaType", mediaType);
                BindText(cmd, "@TriggerType", triggerType.ToLowerInvariant());
                BindNullableText(cmd, "@FromStatus", fromStatus?.ToLowerInvariant());
                BindNullableText(cmd, "@ToStatus", toStatus?.ToLowerInvariant());
                BindText(cmd, "@Result", success ? "success" : "failed");
                BindNullableText(cmd, "@ErrorMessage", errorMessage);
            }, cancellationToken);
        }

        // ── Reader methods for new schema types ───────────────────────────────────────────────

        private static MediaItem ReadMediaItem(IResultSet r)
        {
            var idTypeStr = r.GetString(1);
            var idValue = r.GetString(2);
            var idType = MediaIdTypeExtensions.Parse(idTypeStr);

            return new MediaItem
            {
                Id = r.GetString(0),
                PrimaryId = new MediaId(idType, idValue),
                MediaType = r.GetString(3),
                Title = r.GetString(4),
                Year = r.IsDBNull(5) ? null : (int?)r.GetInt(5),
                Status = Enum.Parse<ItemStatus>(r.GetString(6), ignoreCase: true),
                FailureReason = r.IsDBNull(7) ? FailureReason.None : Enum.Parse<FailureReason>(r.GetString(7), ignoreCase: true),
                Saved = r.GetInt(8) == 1,
                SavedAt = r.IsDBNull(9) ? null : DateTimeOffset.Parse(r.GetString(9)),
                Blocked = r.GetInt(10) == 1,
                BlockedAt = r.IsDBNull(11) ? null : DateTimeOffset.Parse(r.GetString(11)),
                CreatedAt = DateTimeOffset.Parse(r.GetString(12)),
                UpdatedAt = DateTimeOffset.Parse(r.GetString(13)),
                GraceStartedAt = r.IsDBNull(14) ? null : DateTimeOffset.Parse(r.GetString(14)),
                Superseded = r.GetInt(15) == 1,
                SupersededConflict = r.GetInt(16) == 1,
                SupersededAt = r.IsDBNull(17) ? null : DateTimeOffset.Parse(r.GetString(17)),
                EmbyItemId = r.GetString(18),
                EmbyIndexedAt = r.IsDBNull(19) ? null : DateTimeOffset.Parse(r.GetString(19)),
                StrmPath = r.GetString(20),
                WatchProgressPct = r.GetInt(21),
                Favorited = r.GetInt(22) == 1
            };
        }

        private static Source ReadSource(IResultSet r)
        {
            return new Source
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                Url = r.GetString(2),
                Type = Models.SourceTypeExtensions.Parse(r.GetString(3)),
                Enabled = r.GetInt(4) == 1,
                ShowAsCollection = r.GetInt(5) == 1,
                MaxItems = r.GetInt(6),
                SyncIntervalHours = r.GetInt(7),
                LastSyncedAt = r.IsDBNull(8) ? null : DateTimeOffset.Parse(r.GetString(8)),
                EmbyCollectionId = r.GetString(9),
                CollectionName = r.GetString(10),
                CreatedAt = DateTimeOffset.Parse(r.GetString(11)),
                UpdatedAt = DateTimeOffset.Parse(r.GetString(12))
            };
        }

        private static Models.Collection ReadCollection(IResultSet r)
        {
            return new Models.Collection
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                EmbyCollectionId = r.GetString(2),
                SourceId = r.GetString(3),
                Enabled = r.GetInt(4) == 1,
                CollectionName = r.IsDBNull(5) ? null : r.GetString(5),
                LastSyncedAt = r.IsDBNull(6) ? null : DateTimeOffset.Parse(r.GetString(6)),
                CreatedAt = DateTimeOffset.Parse(r.GetString(7)),
                UpdatedAt = DateTimeOffset.Parse(r.GetString(8))
            };
        }

        /// <summary>
        /// Gets all media items from database.
        /// </summary>
        public async Task<List<MediaItem>> GetAllMediaItemsAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, primary_id_type, primary_id_value, media_type, title, year,
                       status, failure_reason, saved, saved_at,
                       blocked, blocked_at, created_at, updated_at, grace_started_at,
                       superseded, superseded_conflict, superseded_at,
                       emby_item_id, emby_indexed_at, strm_path,
                       watch_progress_pct, favorited
                FROM media_items;";

            return await QueryListAsync(sql, null, ReadMediaItem);
        }

        /// <summary>
        /// Checks if a media item exists by primary ID.
        /// </summary>
        public Task<bool> MediaItemExistsByPrimaryIdAsync(MediaId mediaId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT 1 FROM media_items
                WHERE primary_id_type = @IdType
                  AND primary_id_value = @IdValue
                LIMIT 1;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindText(stmt, "@IdType", mediaId.Type.ToLowerString());
            BindText(stmt, "@IdValue", mediaId.Value);
            return Task.FromResult(stmt.AsRows().Any());
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  cached_streams — pre-cached stream metadata
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Resolves the TMDB ID for an IMDB ID by joining media_item_ids.
        /// Returns null if no TMDB ID is found.
        /// </summary>
        public async Task<string?> GetTmdbIdForAioIdAsync(string aioId)
        {
            const string sql = @"
                SELECT tmdb_ids.id_value
                FROM media_item_ids AS imdb_ids
                INNER JOIN media_item_ids AS tmdb_ids
                    ON tmdb_ids.media_item_id = imdb_ids.media_item_id
                    AND tmdb_ids.id_type = 'tmdb'
                WHERE lower(imdb_ids.id_type) = 'imdb'
                  AND lower(imdb_ids.id_value) = lower(@aio_id)
                LIMIT 1";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@aio_id", aioId),
                r => r.GetString(0)).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets a cached stream entry by its TMDB key from stream_resolution_cache.
        /// Returns null if not found or expired.
        /// </summary>
        public async Task<CachedStreamEntry?> GetCachedStreamsByTmdbKeyAsync(string tmdbKey)
        {
            const string sql = @"
                SELECT tmdb_key, aio_id, media_type, season, episode, item_id,
                       variants_json, resolved_at, expires_at, status, manifest_source
                FROM stream_resolution_cache
                WHERE tmdb_key = @tmdb_key
                  AND rank = 0
                  AND status = 'valid'
                  AND expires_at > datetime('now')
                LIMIT 1";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@tmdb_key", tmdbKey),
                ReadCachedStreamEntry).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets a cached stream entry by IMDB ID (+ optional season/episode).
        /// Fallback when TMDB key is not known.
        /// </summary>
        public async Task<CachedStreamEntry?> GetCachedStreamsByAioIdAsync(
            string aioId, int? season, int? episode)
        {
            const string sql = @"
                SELECT tmdb_key, aio_id, media_type, season, episode, item_id,
                       variants_json, resolved_at, expires_at, status, manifest_source
                FROM stream_resolution_cache
                WHERE aio_id = @aio_id
                  AND rank = 0
                  AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
                  AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL))
                  AND status = 'valid'
                  AND expires_at > datetime('now')
                LIMIT 1";

            return await QuerySingleAsync(sql, cmd =>
            {
                BindText(cmd, "@aio_id", aioId);
                BindNullableInt(cmd, "@season", season);
                BindNullableInt(cmd, "@episode", episode);
            }, ReadCachedStreamEntry).ConfigureAwait(false);
        }

        /// <summary>Inserts or replaces a cached stream entry.</summary>
        public async Task UpsertCachedStreamAsync(CachedStreamEntry entry)
        {
            const string sql = @"
                UPDATE stream_resolution_cache
                SET tmdb_key        = @tmdb_key,
                    media_type      = @media_type,
                    item_id         = @item_id,
                    variants_json   = @variants,
                    subtitles_json  = @subtitles_json,
                    expires_at      = @expires_at,
                    status          = @status,
                    manifest_source = @manifest_source,
                    updated_at      = datetime('now')
                WHERE aio_id = @aio_id
                  AND rank = 0
                  AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
                  AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL))";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@tmdb_key", entry.TmdbKey);
                BindText(cmd, "@aio_id", entry.AioId);
                BindText(cmd, "@media_type", entry.MediaType);
                BindNullableInt(cmd, "@season", entry.Season);
                BindNullableInt(cmd, "@episode", entry.Episode);
                BindNullableText(cmd, "@item_id", entry.ItemId);
                BindText(cmd, "@variants", entry.VariantsJson);
                BindNullableText(cmd, "@subtitles_json", entry.SubtitlesJson);
                BindText(cmd, "@cached_at", entry.CachedAt);
                BindText(cmd, "@expires_at", entry.ExpiresAt);
                BindText(cmd, "@status", entry.Status);
                BindNullableText(cmd, "@manifest_source", entry.ManifestSource);
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Marks cached_streams entries as expired for a specific item.
        /// Used by smart refresh when Emby metadata is updated.
        /// </summary>
        public async Task InvalidateCachedStreamAsync(string aioId, int? season, int? episode)
        {
            const string sql = @"
                UPDATE stream_resolution_cache
                SET status = 'expired', updated_at = datetime('now')
                WHERE aio_id = @id
                  AND (@season IS NULL AND season IS NULL OR season = @season)
                  AND (@episode IS NULL AND episode IS NULL OR episode = @episode)";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id", aioId);
                BindNullableInt(cmd, "@season", season);
                BindNullableInt(cmd, "@episode", episode);
            }).ConfigureAwait(false);
        }
        /// <summary>
        /// Returns media items that have no corresponding cached_streams row.
        /// For movies: one row per movie. For series: expands episodes from
        /// catalog_items.videos_json via join on IMDB.
        /// </summary>
        public async Task<List<UncachedItem>> GetUncachedItemsAsync(
            int limit, CancellationToken ct)
        {
            // Movies: media_items with media_type='movie' that are active and have no cached_streams row
            const string moviesSql = @"
                SELECT DISTINCT
                    ids.id_value AS imdb_id,
                    tmdb_ids.id_value AS tmdb_id,
                    'movie' AS media_type,
                    NULL AS season,
                    NULL AS episode,
                    mi.title
                FROM media_items mi
                INNER JOIN media_item_ids ids
                    ON ids.media_item_id = mi.id AND ids.id_type = 'imdb'
                LEFT JOIN media_item_ids tmdb_ids
                    ON tmdb_ids.media_item_id = mi.id AND tmdb_ids.id_type = 'tmdb'
                WHERE mi.media_type = 'movie'
                  AND mi.status IN ('active','indexed','created')
                  AND mi.blocked = 0
                  AND NOT EXISTS (
                      SELECT 1 FROM stream_resolution_cache cs
                      WHERE cs.aio_id = ids.id_value
                        AND cs.media_type = 'movie'
                        AND cs.status = 'valid'
                        AND cs.expires_at > datetime('now')
                  )
                ORDER BY mi.created_at DESC
                LIMIT @limit";

            // Series: get series items and expand their episodes from catalog_items.videos_json
            const string seriesSql = @"
                SELECT DISTINCT
                    ids.id_value AS imdb_id,
                    tmdb_ids.id_value AS tmdb_id,
                    'series' AS media_type,
                    (season_num) AS season,
                    (episode_num) AS episode,
                    mi.title
                FROM media_items mi
                INNER JOIN media_item_ids ids
                    ON ids.media_item_id = mi.id AND ids.id_type = 'imdb'
                LEFT JOIN media_item_ids tmdb_ids
                    ON tmdb_ids.media_item_id = mi.id AND tmdb_ids.id_type = 'tmdb'
                INNER JOIN catalog_items ci
                    ON lower(ci.aio_id) = lower(ids.id_value)
                CROSS JOIN json_each(
                    CASE
                        WHEN ci.videos_json IS NOT NULL AND ci.videos_json != ''
                        THEN ci.videos_json
                        ELSE '[]'
                    END
                ) AS video
                CROSS JOIN json_extract(video.value, '$.season') AS season_num
                CROSS JOIN json_extract(video.value, '$.episode') AS episode_num
                WHERE mi.media_type = 'series'
                  AND mi.status IN ('active','indexed','created')
                  AND mi.blocked = 0
                  AND season_num IS NOT NULL
                  AND episode_num IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM stream_resolution_cache cs
                      WHERE cs.aio_id = ids.id_value
                        AND cs.media_type = 'series'
                        AND cs.season = season_num
                        AND cs.episode = episode_num
                        AND cs.status = 'valid'
                        AND cs.expires_at > datetime('now')
                  )
                ORDER BY mi.created_at DESC
                LIMIT @limit";

            var results = new List<UncachedItem>();

            // Fetch movies
            var movies = await QueryListAsync(moviesSql,
                cmd => BindInt(cmd, "@limit", limit / 2),
                r => new UncachedItem
                {
                    AioId = r.GetString(0),
                    TmdbId = r.IsDBNull(1) ? null : r.GetString(1),
                    MediaType = r.GetString(2),
                    Title = r.IsDBNull(5) ? "" : r.GetString(5),
                }).ConfigureAwait(false);
            results.AddRange(movies);

            if (ct.IsCancellationRequested) return results;

            // Fetch series episodes
            var remaining = Math.Max(0, limit - results.Count);
            if (remaining > 0)
            {
                var episodes = await QueryListAsync(seriesSql,
                    cmd => BindInt(cmd, "@limit", remaining),
                    r => new UncachedItem
                    {
                        AioId = r.GetString(0),
                        TmdbId = r.IsDBNull(1) ? null : r.GetString(1),
                        MediaType = r.GetString(2),
                        Season = r.IsDBNull(3) ? (int?)null : r.GetInt(3),
                        Episode = r.IsDBNull(4) ? (int?)null : r.GetInt(4),
                        Title = r.IsDBNull(5) ? "" : r.GetString(5),
                    }).ConfigureAwait(false);
                results.AddRange(episodes);
            }

            return results;
        }

        private static CachedStreamEntry ReadCachedStreamEntry(IResultSet r)
        {
            return new CachedStreamEntry
            {
                TmdbKey = r.IsDBNull(0) ? "" : r.GetString(0),
                AioId = r.GetString(1),
                MediaType = r.IsDBNull(2) ? "movie" : r.GetString(2),
                Season = r.IsDBNull(3) ? (int?)null : r.GetInt(3),
                Episode = r.IsDBNull(4) ? (int?)null : r.GetInt(4),
                ItemId = r.IsDBNull(5) ? null : r.GetString(5),
                VariantsJson = r.IsDBNull(6) ? "[]" : r.GetString(6),
                CachedAt = r.IsDBNull(7) ? "" : r.GetString(7),
                ExpiresAt = r.GetString(8),
                Status = r.GetString(9),
                ManifestSource = r.IsDBNull(10) ? null : r.GetString(10),
            };
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  blocked_items — admin-managed block list
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Checks if an item is blocked by any of its IDs (OR match).
        /// Null IDs are skipped. Only checks active blocks (unblocked_at IS NULL).
        /// </summary>
        public Task<bool> IsBlockedAsync(string? aioId, string? tmdbId, string? anilistId)
        {
            var conditions = new List<string>();
            if (!string.IsNullOrEmpty(aioId))
                conditions.Add("lower(aio_id) = lower(@aio_id)");
            if (!string.IsNullOrEmpty(tmdbId))
                conditions.Add("lower(tmdb_id) = lower(@tmdb_id)");
            if (!string.IsNullOrEmpty(anilistId))
                conditions.Add("lower(anilist_id) = lower(@anilist_id)");

            if (conditions.Count == 0)
                return Task.FromResult(false);

            var sql = $"SELECT COUNT(*) FROM blocked_items WHERE ({string.Join(" OR ", conditions)}) AND unblocked_at IS NULL";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            if (!string.IsNullOrEmpty(aioId)) BindText(stmt, "@aio_id", aioId);
            if (!string.IsNullOrEmpty(tmdbId)) BindText(stmt, "@tmdb_id", tmdbId);
            if (!string.IsNullOrEmpty(anilistId)) BindText(stmt, "@anilist_id", anilistId);

            foreach (var row in stmt.AsRows())
                return Task.FromResult(row.GetInt(0) > 0);

            return Task.FromResult(false);
        }

        /// <summary>Inserts or updates a blocked item.</summary>
        public async Task UpsertBlockedItemAsync(
            string? aioId, string? tmdbId, string? anilistId,
            string title, string mediaType, string blockedBy)
        {
            const string sql = @"
                INSERT INTO blocked_items (aio_id, tmdb_id, anilist_id, title, media_type, blocked_at, blocked_by)
                VALUES (@aio_id, @tmdb_id, @anilist_id, @title, @media_type, datetime('now'), @blocked_by)";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindNullableText(cmd, "@aio_id", aioId);
                BindNullableText(cmd, "@tmdb_id", tmdbId);
                BindNullableText(cmd, "@anilist_id", anilistId);
                BindText(cmd, "@title", title);
                BindText(cmd, "@media_type", mediaType);
                BindText(cmd, "@blocked_by", blockedBy);
            }).ConfigureAwait(false);
        }

        /// <summary>Unblocks an item by setting unblocked_at and unblocked_by.</summary>
        public async Task UnblockItemAsync(long id, string unblockedBy)
        {
            const string sql = @"
                UPDATE blocked_items
                SET unblocked_at = datetime('now'),
                    unblocked_by = @unblocked_by
                WHERE id = @id";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindInt(cmd, "@id", (int)id);
                BindText(cmd, "@unblocked_by", unblockedBy);
            }).ConfigureAwait(false);
        }

        /// <summary>Gets paginated active blocked items.</summary>
        public async Task<List<BlockedItem>> GetBlockedItemsAsync(int skip, int limit)
        {
            const string sql = @"
                SELECT id, aio_id, tmdb_id, anilist_id, title, media_type,
                       blocked_at, blocked_by, unblocked_at, unblocked_by
                FROM blocked_items
                WHERE unblocked_at IS NULL
                ORDER BY blocked_at DESC
                LIMIT @limit OFFSET @skip";

            return await QueryListAsync(sql, cmd =>
            {
                BindInt(cmd, "@limit", limit);
                BindInt(cmd, "@skip", skip);
            }, r => new BlockedItem
            {
                Id = r.GetInt(0),
                AioId = r.IsDBNull(1) ? null : r.GetString(1),
                TmdbId = r.IsDBNull(2) ? null : r.GetString(2),
                AnilistId = r.IsDBNull(3) ? null : r.GetString(3),
                Title = r.GetString(4),
                MediaType = r.GetString(5),
                BlockedAt = r.GetString(6),
                BlockedBy = r.GetString(7),
                UnblockedAt = r.IsDBNull(8) ? null : r.GetString(8),
                UnblockedBy = r.IsDBNull(9) ? null : r.GetString(9),
            }).ConfigureAwait(false);
        }

        public Task<int> GetNeedsEnrichCountAsync(CancellationToken ct = default)
            => QueryScalarIntAsync(
                "SELECT COUNT(*) FROM catalog_items WHERE enrichment_status = 'NeedsEnrich' AND removed_at IS NULL;",
                ct);

        public Task<int> GetBlockedCountAsync(CancellationToken ct = default)
            => QueryScalarIntAsync(
                "SELECT COUNT(*) FROM catalog_items WHERE enrichment_status = 'Blocked' AND removed_at IS NULL;",
                ct);
    }
}
