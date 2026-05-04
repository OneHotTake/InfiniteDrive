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
        // ── New Schema Operations (media_items, sources, source_memberships, etc.) ─────────────────

        /// <summary>
        /// Gets a media item by ID.
        /// </summary>
        public async Task<MediaItem?> GetMediaItemAsync(string itemId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, primary_id_type, primary_id, media_type, title, year,
                       status, failure_reason, saved, saved_at,
                       blocked, blocked_at, created_at, updated_at, grace_started_at,
                       superseded, superseded_conflict, superseded_at,
                       emby_item_id, emby_indexed_at, strm_path, nfo_path,
                       watch_progress_pct, favorited
                FROM media_items
                WHERE id = @ItemId
                LIMIT 1;";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@ItemId", itemId),
                ReadMediaItem);
        }

        /// <summary>
        /// Finds a media item by provider ID (TMDB, IMDB, TVDB, AniList, AniDB, Kitsu).
        /// </summary>
        public async Task<MediaItem?> FindMediaItemByProviderIdAsync(string idType, string idValue, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT mi.id, mi.primary_id_type, mi.primary_id, mi.media_type, mi.title, mi.year,
                       mi.status, mi.failure_reason, mi.saved, mi.saved_at,
                       mi.blocked, mi.blocked_at, mi.created_at, mi.updated_at, mi.grace_started_at,
                       mi.superseded, mi.superseded_conflict, mi.superseded_at,
                       mi.emby_item_id, mi.emby_indexed_at, mi.strm_path, mi.nfo_path,
                       mi.watch_progress_pct, mi.favorited
                FROM media_items mi
                INNER JOIN media_item_ids mii ON mi.id = mii.media_item_id
                WHERE mii.id_type = @IdType
                  AND mii.id_value = @IdValue
                  AND mii.is_primary = 1
                LIMIT 1;";

            return await QuerySingleAsync(sql,
                cmd =>
                {
                    BindText(cmd, "@IdType", idType.ToLowerInvariant());
                    BindText(cmd, "@IdValue", idValue);
                },
                ReadMediaItem);
        }

        /// <summary>
        /// Finds media items by source membership.
        /// </summary>
        public async Task<List<MediaItem>> FindMediaItemsBySourceAsync(string sourceId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT mi.id, mi.primary_id_type, mi.primary_id, mi.media_type, mi.title, mi.year,
                       mi.status, mi.failure_reason, mi.saved, mi.saved_at,
                       mi.blocked, mi.blocked_at, mi.created_at, mi.updated_at, mi.grace_started_at,
                       mi.superseded, mi.superseded_conflict, mi.superseded_at,
                       mi.emby_item_id, mi.emby_indexed_at, mi.strm_path, mi.nfo_path,
                       mi.watch_progress_pct, mi.favorited
                FROM media_items mi
                INNER JOIN source_memberships sm ON mi.id = sm.media_item_id
                WHERE sm.source_id = @SourceId;";

            return await QueryListAsync(sql,
                cmd => BindText(cmd, "@SourceId", sourceId),
                ReadMediaItem);
        }

        /// <summary>
        /// Gets media items by saved state.
        /// </summary>
        public async Task<List<MediaItem>> GetItemsBySavedAsync(bool saved, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT mi.id, mi.primary_id_type, mi.primary_id, mi.media_type, mi.title, mi.year,
                       mi.status, mi.failure_reason, mi.saved, mi.saved_at,
                       mi.blocked, mi.blocked_at, mi.created_at, mi.updated_at, mi.grace_started_at,
                       mi.superseded, mi.superseded_conflict, mi.superseded_at,
                       mi.emby_item_id, mi.emby_indexed_at, mi.strm_path, mi.nfo_path,
                       mi.watch_progress_pct, mi.favorited
                FROM media_items mi
                WHERE mi.saved = @Saved
                ORDER BY mi.title;";

            return await QueryListAsync(sql,
                cmd => BindInt(cmd, "@Saved", saved ? 1 : 0),
                ReadMediaItem);
        }

        // ── Per-user saves (user_item_saves) ────────────────────────────────

        /// <summary>
        /// Gets saved media items for a specific user.
        /// </summary>
        public async Task<List<MediaItem>> GetSavedItemsByUserAsync(string userId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT mi.id, mi.primary_id_type, mi.primary_id, mi.media_type, mi.title, mi.year,
                       mi.status, mi.failure_reason, mi.saved, mi.saved_at,
                       mi.blocked, mi.blocked_at, mi.created_at, mi.updated_at, mi.grace_started_at,
                       mi.superseded, mi.superseded_conflict, mi.superseded_at,
                       mi.emby_item_id, mi.emby_indexed_at, mi.strm_path, mi.nfo_path,
                       mi.watch_progress_pct, mi.favorited
                FROM user_item_saves us
                JOIN media_items mi ON us.media_item_id = mi.id
                WHERE us.user_id = @UserId
                ORDER BY us.saved_at DESC;";

            return await QueryListAsync(sql,
                cmd => BindText(cmd, "@UserId", userId),
                ReadMediaItem);
        }

        /// <summary>
        /// Inserts a per-user save (idempotent via INSERT OR IGNORE).
        /// </summary>
        public async Task UpsertUserSaveAsync(string userId, string mediaItemId, string? saveReason, int? savedSeason, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT OR IGNORE INTO user_item_saves (user_id, media_item_id, save_reason, saved_season)
                VALUES (@UserId, @MediaItemId, @SaveReason, @SavedSeason);";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@UserId", userId);
                BindText(cmd, "@MediaItemId", mediaItemId);
                BindNullableText(cmd, "@SaveReason", saveReason);
                BindNullableInt(cmd, "@SavedSeason", savedSeason);
            }, cancellationToken);
        }

        /// <summary>
        /// Deletes a per-user save.
        /// </summary>
        public async Task DeleteUserSaveAsync(string userId, string mediaItemId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                DELETE FROM user_item_saves
                WHERE user_id = @UserId AND media_item_id = @MediaItemId;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@UserId", userId);
                BindText(cmd, "@MediaItemId", mediaItemId);
            }, cancellationToken);
        }

        /// <summary>
        /// Checks if a user has saved a specific item.
        /// </summary>
        public Task<bool> HasUserSaveAsync(string userId, string mediaItemId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT EXISTS(
                    SELECT 1 FROM user_item_saves
                    WHERE user_id = @UserId AND media_item_id = @MediaItemId
                );";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindText(stmt, "@UserId", userId);
            BindText(stmt, "@MediaItemId", mediaItemId);
            return Task.FromResult(stmt.AsRows().Any());
        }

        /// <summary>
        /// Re-syncs the denormalized saved flag on media_items from user_item_saves.
        /// Sets saved=1 if any user save exists, saved=0 otherwise. Updates saved_at.
        /// </summary>
        public async Task SyncGlobalSavedFlagAsync(string mediaItemId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE media_items
                SET saved = (SELECT CASE WHEN EXISTS(
                    SELECT 1 FROM user_item_saves WHERE media_item_id = @Id
                ) THEN 1 ELSE 0 END),
                saved_at = (SELECT MAX(saved_at) FROM user_item_saves WHERE media_item_id = @Id)
                WHERE id = @Id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@Id", mediaItemId);
            }, cancellationToken);
        }

        /// <summary>
        /// Deletes all user save records for a given media item.
        /// Used by admin block action to clear all per-user saves.
        /// </summary>
        public async Task DeleteAllUserSavesForItemAsync(string mediaItemId, CancellationToken ct = default)
        {
            const string sql = @"
                DELETE FROM user_item_saves
                WHERE media_item_id = @id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id", mediaItemId);
            }, ct);
        }

        /// <summary>
        /// Gets a media item by its primary ID (e.g. IMDB ID).
        /// Used by admin block action to resolve IMDB ID → media_item.
        /// </summary>
        public async Task<MediaItem?> GetMediaItemByPrimaryIdAsync(string primaryId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, primary_id_type, primary_id, media_type, title, year,
                       status, failure_reason, saved, saved_at,
                       blocked, blocked_at, created_at, updated_at, grace_started_at,
                       superseded, superseded_conflict, superseded_at,
                       emby_item_id, emby_indexed_at, strm_path, nfo_path,
                       watch_progress_pct, favorited
                FROM media_items
                WHERE primary_id = @PrimaryId
                LIMIT 1;";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@PrimaryId", primaryId),
                ReadMediaItem);
        }

        /// <summary>
        /// Gets a media item by its internal UUID (media_items.id).
        /// Used by AdminService when blocking/searching by internal ID.
        /// </summary>
        public async Task<MediaItem?> GetMediaItemByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, primary_id_type, primary_id, media_type, title, year,
                       status, failure_reason, saved, saved_at,
                       blocked, blocked_at, created_at, updated_at, grace_started_at,
                       superseded, superseded_conflict, superseded_at,
                       emby_item_id, emby_indexed_at, strm_path, nfo_path,
                       watch_progress_pct, favorited
                FROM media_items
                WHERE id = @Id
                LIMIT 1;";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@Id", id),
                ReadMediaItem);
        }

        /// <summary>
        /// Searches media items by title, returning non-blocked items only.
        /// Used by AdminService for the block search UI.
        /// </summary>
        public async Task<List<MediaItem>> SearchMediaItemsByTitleAsync(string query, int limit, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, primary_id_type, primary_id, media_type, title, year,
                       status, failure_reason, saved, saved_at,
                       blocked, blocked_at, created_at, updated_at, grace_started_at,
                       superseded, superseded_conflict, superseded_at,
                       emby_item_id, emby_indexed_at, strm_path, nfo_path,
                       watch_progress_pct, favorited
                FROM media_items
                WHERE title LIKE @Query
                  AND blocked = 0
                ORDER BY title
                LIMIT @Limit;";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@Query", $"%{query}%");
                BindInt(cmd, "@Limit", limit);
            }, ReadMediaItem);
        }

        /// <summary>
        /// Blocks a catalog item by its IMDB ID.
        /// Sets blocked_at and blocked_by, clearing nfo_status.
        /// </summary>
        public async Task BlockCatalogItemByAioIdAsync(string imdbId, string blockedBy, CancellationToken ct = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET blocked_at  = datetime('now'),
                    blocked_by  = @blocked_by,
                    updated_at  = datetime('now')
                WHERE imdb_id = @imdb_id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@imdb_id", imdbId);
                BindText(cmd, "@blocked_by", blockedBy);
            }, ct);

            _logger.LogInformation("[DatabaseManager] Blocked catalog item {ImdbId}", imdbId);
        }

        /// <summary>
        /// Gets orphaned user saves where the media_item no longer exists.
        /// For Marvin cleanup.
        /// </summary>
        public Task<List<(string SaveId, string UserId, string MediaItemId)>> GetOrphanedUserSavesAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT us.id, us.user_id, us.media_item_id
                FROM user_item_saves us
                LEFT JOIN media_items mi ON us.media_item_id = mi.id
                WHERE mi.id IS NULL;";

            var results = new List<(string, string, string)>();
            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            foreach (var row in stmt.AsRows())
            {
                results.Add((row.GetString(0), row.GetString(1), row.GetString(2)));
            }
            return Task.FromResult(results);
        }

        /// <summary>
        /// Deletes a user save by row ID. For Marvin cleanup.
        /// </summary>
        public async Task DeleteUserSaveByIdAsync(string saveId, CancellationToken cancellationToken = default)
        {
            const string sql = "DELETE FROM user_item_saves WHERE id = @Id;";
            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@Id", saveId);
            }, cancellationToken);
        }

        /// <summary>
        /// Gets media items with active grace period.
        /// </summary>
        public async Task<List<MediaItem>> GetItemsByGraceStartedAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT mi.id, mi.primary_id_type, mi.primary_id, mi.media_type, mi.title, mi.year,
                       mi.status, mi.failure_reason, mi.saved, mi.saved_at,
                       mi.blocked, mi.blocked_at, mi.created_at, mi.updated_at, mi.grace_started_at,
                       mi.superseded, mi.superseded_conflict, mi.superseded_at,
                       mi.emby_item_id, mi.emby_indexed_at, mi.strm_path, mi.nfo_path,
                       mi.watch_progress_pct, mi.favorited
                FROM media_items mi
                WHERE mi.grace_started_at IS NOT NULL
                ORDER BY mi.grace_started_at;";

            return await QueryListAsync(sql, null, ReadMediaItem);
        }

        /// <summary>
        /// Gets media items with pagination and filtering (for API).
        /// </summary>
        public async Task<List<MediaItem>> GetItemsAsync(
            Models.ItemStatus? status,
            string orderBy,
            string orderDirection,
            int limit,
            int offset,
            CancellationToken cancellationToken = default)
        {
            var whereClause = status.HasValue ? "WHERE mi.status = @Status" : "";
            var orderClause = $"ORDER BY mi.{orderBy} {orderDirection.ToUpperInvariant()}";

            const string sqlTemplate = @"
                SELECT mi.id, mi.primary_id_type, mi.primary_id, mi.media_type, mi.title, mi.year,
                       mi.status, mi.failure_reason, mi.saved, mi.saved_at,
                       mi.blocked, mi.blocked_at, mi.created_at, mi.updated_at, mi.grace_started_at,
                       mi.superseded, mi.superseded_conflict, mi.superseded_at,
                       mi.emby_item_id, mi.emby_indexed_at, mi.strm_path, mi.nfo_path,
                       mi.watch_progress_pct, mi.favorited
                FROM media_items mi
                {0}
                {1}
                LIMIT @Limit OFFSET @Offset;";

            var sql = string.Format(sqlTemplate, whereClause, orderClause);

            return await QueryListAsync(sql, cmd =>
            {
                if (status.HasValue)
                    BindText(cmd, "@Status", status.Value.ToString());
                BindInt(cmd, "@Limit", limit);
                BindInt(cmd, "@Offset", offset);
            }, ReadMediaItem);
        }

        /// <summary>
        /// Gets total count of media items (for API pagination).
        /// </summary>
        public Task<int> GetItemCountAsync(Models.ItemStatus? status, CancellationToken cancellationToken = default)
        {
            var whereClause = status.HasValue ? "WHERE status = @Status" : "";
            var sql = $"SELECT COUNT(*) FROM media_items {whereClause};";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            if (status.HasValue)
                BindText(stmt, "@Status", status.Value.ToString());

            foreach (var row in stmt.AsRows())
                return Task.FromResult(row.GetInt(0));
            return Task.FromResult(0);
        }

        /// <summary>
        /// Searches media items by query string (for API).
        /// </summary>
        public async Task<List<MediaItem>> SearchItemsAsync(string query, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT mi.id, mi.primary_id_type, mi.primary_id, mi.media_type, mi.title, mi.year,
                       mi.status, mi.failure_reason, mi.saved, mi.saved_at,
                       mi.blocked, mi.blocked_at, mi.created_at, mi.updated_at, mi.grace_started_at,
                       mi.superseded, mi.superseded_conflict, mi.superseded_at,
                       mi.emby_item_id, mi.emby_indexed_at, mi.strm_path, mi.nfo_path,
                       mi.watch_progress_pct, mi.favorited
                FROM media_items mi
                WHERE mi.title LIKE @Query
                   OR mi.primary_id LIKE @Query
                ORDER BY mi.title
                LIMIT 100;";

            var searchPattern = $"%{query}%";
            return await QueryListAsync(sql, cmd => BindText(cmd, "@Query", searchPattern), ReadMediaItem);
        }

        /// <summary>
        /// Inserts or updates a media item.
        /// </summary>
        public async Task UpsertMediaItemAsync(MediaItem item, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO media_items
                    (id, primary_id_type, primary_id, media_type, title, year,
                     status, failure_reason, saved, saved_at,
                     blocked, blocked_at, created_at, updated_at, grace_started_at,
                     superseded, superseded_conflict, superseded_at,
                     emby_item_id, emby_indexed_at, strm_path, nfo_path,
                     watch_progress_pct, favorited)
                VALUES
                    (@id, @primary_id_type, @primary_id, @media_type, @title, @year,
                     @status, @failure_reason, @saved, @saved_at,
                     @blocked, @blocked_at, @created_at, @updated_at, @grace_started_at,
                     @superseded, @superseded_conflict, @superseded_at,
                     @emby_item_id, @emby_indexed_at, @strm_path, @nfo_path,
                     @watch_progress_pct, @favorited)
                ON CONFLICT(id) DO UPDATE SET
                    status = excluded.status,
                    failure_reason = excluded.failure_reason,
                    saved = excluded.saved,
                    saved_at = excluded.saved_at,
                    blocked = excluded.blocked,
                    blocked_at = excluded.blocked_at,
                    updated_at = excluded.updated_at,
                    grace_started_at = excluded.grace_started_at,
                    superseded = excluded.superseded,
                    superseded_conflict = excluded.superseded_conflict,
                    superseded_at = excluded.superseded_at,
                    emby_item_id = excluded.emby_item_id,
                    emby_indexed_at = excluded.emby_indexed_at,
                    strm_path = excluded.strm_path,
                    nfo_path = excluded.nfo_path,
                    watch_progress_pct = excluded.watch_progress_pct,
                    favorited = excluded.favorited;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id", item.Id);
                BindText(cmd, "@primary_id_type", item.PrimaryId.Type.ToLowerString());
                BindText(cmd, "@primary_id", item.PrimaryId.Value);
                BindText(cmd, "@media_type", item.MediaType);
                BindText(cmd, "@title", item.Title);
                BindNullableInt(cmd, "@year", item.Year);
                BindText(cmd, "@status", item.Status.ToString().ToLowerInvariant());
                BindNullableText(cmd, "@failure_reason", item.FailureReason != FailureReason.None ? item.FailureReason.ToString().ToLowerInvariant() : null);
                BindInt(cmd, "@saved", item.Saved ? 1 : 0);
                BindNullableText(cmd, "@saved_at", item.SavedAt?.ToString("o"));
                BindInt(cmd, "@blocked", item.Blocked ? 1 : 0);
                BindNullableText(cmd, "@blocked_at", item.BlockedAt?.ToString("o"));
                BindText(cmd, "@created_at", item.CreatedAt.ToString("o"));
                BindText(cmd, "@updated_at", item.UpdatedAt.ToString("o"));
                BindNullableText(cmd, "@grace_started_at", item.GraceStartedAt?.ToString("o"));
                BindInt(cmd, "@superseded", item.Superseded ? 1 : 0);
                BindInt(cmd, "@superseded_conflict", item.SupersededConflict ? 1 : 0);
                BindNullableText(cmd, "@superseded_at", item.SupersededAt?.ToString("o"));
                BindNullableText(cmd, "@emby_item_id", item.EmbyItemId);
                BindNullableText(cmd, "@emby_indexed_at", item.EmbyIndexedAt?.ToString("o"));
                BindNullableText(cmd, "@strm_path", item.StrmPath);
                BindNullableText(cmd, "@nfo_path", item.NfoPath);
                BindInt(cmd, "@watch_progress_pct", item.WatchProgressPct);
                BindInt(cmd, "@favorited", item.Favorited ? 1 : 0);
            }, cancellationToken);
        }

        /// <summary>
        /// Checks if an item has any enabled source (Coalition rule).
        /// Uses single JOIN query per spec.
        /// </summary>
        public Task<bool> ItemHasEnabledSourceAsync(string itemId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT 1 FROM source_memberships sm
                JOIN sources s ON sm.source_id = s.id
                WHERE sm.media_item_id = @ItemId
                  AND s.enabled = 1
                LIMIT 1;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindText(stmt, "@ItemId", itemId);
            return Task.FromResult(stmt.AsRows().Any());
        }

        /// <summary>
        /// Updates a media item.
        /// </summary>
        public async Task UpdateMediaItemAsync(MediaItem item, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE media_items
                SET primary_id_type = @primary_id_type,
                    primary_id = @primary_id,
                    media_type = @media_type,
                    title = @title,
                    year = @year,
                    status = @status,
                    failure_reason = @failure_reason,
                    saved = @saved,
                    saved_at = @saved_at,
                    blocked = @blocked,
                    blocked_at = @blocked_at,
                    grace_started_at = @grace_started_at,
                    superseded = @superseded,
                    superseded_conflict = @superseded_conflict,
                    superseded_at = @superseded_at,
                    emby_item_id = @emby_item_id,
                    emby_indexed_at = @emby_indexed_at,
                    strm_path = @strm_path,
                    nfo_path = @nfo_path,
                    watch_progress_pct = @watch_progress_pct,
                    favorited = @favorited,
                    updated_at = datetime('now')
                WHERE id = @id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id", item.Id);
                BindText(cmd, "@primary_id_type", item.PrimaryId.Type.ToLowerString());
                BindText(cmd, "@primary_id", item.PrimaryId.Value);
                BindText(cmd, "@media_type", item.MediaType);
                BindText(cmd, "@title", item.Title);
                BindNullableInt(cmd, "@year", item.Year);
                BindText(cmd, "@status", item.Status.ToString().ToLowerInvariant());
                BindNullableText(cmd, "@failure_reason", item.FailureReason != FailureReason.None ? item.FailureReason.ToString().ToLowerInvariant() : null);
                BindInt(cmd, "@saved", item.Saved ? 1 : 0);
                BindNullableText(cmd, "@saved_at", item.SavedAt?.ToString("o"));
                BindInt(cmd, "@blocked", item.Blocked ? 1 : 0);
                BindNullableText(cmd, "@blocked_at", item.BlockedAt?.ToString("o"));
                BindNullableText(cmd, "@grace_started_at", item.GraceStartedAt?.ToString("o"));
                BindInt(cmd, "@superseded", item.Superseded ? 1 : 0);
                BindInt(cmd, "@superseded_conflict", item.SupersededConflict ? 1 : 0);
                BindNullableText(cmd, "@superseded_at", item.SupersededAt?.ToString("o"));
                BindNullableText(cmd, "@emby_item_id", item.EmbyItemId);
                BindNullableText(cmd, "@emby_indexed_at", item.EmbyIndexedAt?.ToString("o"));
                BindNullableText(cmd, "@strm_path", item.StrmPath);
                BindNullableText(cmd, "@nfo_path", item.NfoPath);
                BindInt(cmd, "@watch_progress_pct", item.WatchProgressPct);
                BindInt(cmd, "@favorited", item.Favorited ? 1 : 0);
            }, cancellationToken);
        }

        /// <summary>
        /// Gets all sources.
        /// </summary>
        public async Task<List<Source>> GetAllSourcesAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, name, url, type, enabled, show_as_collection,
                       max_items, sync_interval_hours, last_synced_at,
                       emby_collection_id, collection_name, created_at, updated_at
                FROM sources
                ORDER BY name;";

            return await QueryListAsync(sql, null, ReadSource);
        }

        /// <summary>
        /// Gets enabled sources only.
        /// </summary>
        public async Task<List<Source>> GetEnabledSourcesAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, name, url, type, enabled, show_as_collection,
                       max_items, sync_interval_hours, last_synced_at,
                       emby_collection_id, collection_name, created_at, updated_at
                FROM sources
                WHERE enabled = 1
                ORDER BY name;";

            return await QueryListAsync(sql, null, ReadSource);
        }

        /// <summary>
        /// Gets sources with ShowAsCollection = true.
        /// </summary>
        public async Task<List<Source>> GetSourcesWithShowAsCollectionAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, name, url, type, enabled, show_as_collection,
                       max_items, sync_interval_hours, last_synced_at,
                       emby_collection_id, collection_name, created_at, updated_at
                FROM sources
                WHERE show_as_collection = 1
                ORDER BY name;";

            return await QueryListAsync(sql, null, ReadSource);
        }

        /// <summary>
        /// Sets the enabled state of a source.
        /// </summary>
        public async Task SetSourceEnabledAsync(string sourceId, bool enabled, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE sources
                SET enabled = @Enabled,
                    updated_at = datetime('now')
                WHERE id = @SourceId;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@SourceId", sourceId);
                cmd.BindParameters["@Enabled"].Bind(enabled ? 1 : 0);
            }, cancellationToken);
        }

        /// <summary>
        /// Sets the ShowAsCollection flag for a source.
        /// </summary>
        public async Task SetSourceShowAsCollectionAsync(string sourceId, bool show, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE sources
                SET show_as_collection = @Show,
                    updated_at = datetime('now')
                WHERE id = @SourceId;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@SourceId", sourceId);
                cmd.BindParameters["@Show"].Bind(show ? 1 : 0);
            }, cancellationToken);
        }

        /// <summary>
        /// Gets a single source by ID.
        /// </summary>
        public async Task<Source?> GetSourceAsync(string sourceId, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT id, name, url, type, enabled, show_as_collection,
                       max_items, sync_interval_hours, last_synced_at,
                       emby_collection_id, collection_name, created_at, updated_at
                FROM sources
                WHERE id = @SourceId;";

            return await QuerySingleAsync(sql, cmd => BindText(cmd, "@SourceId", sourceId), ReadSource);
        }

        /// <summary>
        /// Upserts a source (inserts or updates).
        /// </summary>
        public async Task UpsertSourceAsync(Source source, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO sources (id, name, url, type, enabled, show_as_collection,
                                   max_items, sync_interval_hours, last_synced_at,
                                   emby_collection_id, collection_name, created_at, updated_at)
                VALUES (@Id, @Name, @Url, @Type, @Enabled, @ShowAsCollection,
                        @MaxItems, @SyncIntervalHours, @LastSyncedAt,
                        @EmbyCollectionId, @CollectionName, @CreatedAt, @UpdatedAt)
                ON CONFLICT(id) DO UPDATE SET
                    name = @Name,
                    url = @Url,
                    type = @Type,
                    enabled = @Enabled,
                    show_as_collection = @ShowAsCollection,
                    max_items = @MaxItems,
                    sync_interval_hours = @SyncIntervalHours,
                    last_synced_at = @LastSyncedAt,
                    emby_collection_id = @EmbyCollectionId,
                    collection_name = @CollectionName,
                    updated_at = @UpdatedAt;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@Id", source.Id);
                BindText(cmd, "@Name", source.Name);
                BindText(cmd, "@Url", source.Url);
                BindText(cmd, "@Type", source.Type.ToString());
                cmd.BindParameters["@Enabled"].Bind(source.Enabled ? 1 : 0);
                cmd.BindParameters["@ShowAsCollection"].Bind(source.ShowAsCollection ? 1 : 0);
                BindInt(cmd, "@MaxItems", source.MaxItems);
                BindInt(cmd, "@SyncIntervalHours", source.SyncIntervalHours);
                BindText(cmd, "@LastSyncedAt", source.LastSyncedAt?.ToString("o"));
                BindText(cmd, "@EmbyCollectionId", source.EmbyCollectionId);
                BindText(cmd, "@CollectionName", source.CollectionName);
                BindText(cmd, "@CreatedAt", source.CreatedAt.ToString("o"));
                BindText(cmd, "@UpdatedAt", source.UpdatedAt.ToString("o"));
            }, cancellationToken);
        }

        /// <summary>
        /// Deletes a source by ID.
        /// </summary>
        public async Task DeleteSourceAsync(string sourceId, CancellationToken cancellationToken = default)
        {
            const string sql = @"DELETE FROM sources WHERE id = @SourceId;";
            await ExecuteWriteAsync(sql, cmd => BindText(cmd, "@SourceId", sourceId), cancellationToken);
        }

        // ── Log Insert Methods (Sprint 120) ─────────────────────────────────────────

        /// <summary>
        /// Inserts a pipeline log entry.
        /// </summary>
        public Task InsertPipelineLogAsync(Models.PipelineLogEntry entry, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO item_pipeline_log (primary_id, primary_id_type, media_type, phase, trigger, success, details, timestamp)
                VALUES (@PrimaryId, @PrimaryIdType, @MediaType, @Phase, @Trigger, @Success, @Details, @Timestamp);";

            return ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@PrimaryId", entry.PrimaryId);
                BindText(cmd, "@PrimaryIdType", entry.PrimaryIdType);
                BindText(cmd, "@MediaType", entry.MediaType);
                BindText(cmd, "@Phase", entry.Phase);
                BindText(cmd, "@Trigger", entry.Trigger);
                cmd.BindParameters["@Success"].Bind(entry.Success ? 1 : 0);
                BindNullableText(cmd, "@Details", entry.Details);
                BindText(cmd, "@Timestamp", entry.Timestamp.ToString("o"));
            }, cancellationToken);
        }

        /// <summary>
        /// Inserts a resolution log entry.
        /// </summary>
        public Task InsertResolutionLogAsync(Models.ResolutionLogEntry entry, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO stream_resolution_log (primary_id, primary_id_type, media_type, media_id, stream_count, selected_stream, duration_ms, timestamp)
                VALUES (@PrimaryId, @PrimaryIdType, @MediaType, @MediaId, @StreamCount, @SelectedStream, @DurationMs, @Timestamp);";

            return ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@PrimaryId", entry.PrimaryId);
                BindText(cmd, "@PrimaryIdType", entry.PrimaryIdType);
                BindText(cmd, "@MediaType", entry.MediaType);
                BindText(cmd, "@MediaId", entry.MediaId);
                BindInt(cmd, "@StreamCount", entry.StreamCount);
                BindNullableText(cmd, "@SelectedStream", entry.SelectedStream);
                BindInt(cmd, "@DurationMs", (int)entry.DurationMs);
                BindText(cmd, "@Timestamp", entry.Timestamp.ToString("o"));
            }, cancellationToken);
        }

        /// <summary>
        /// Gets pipeline log count.
        /// </summary>
        public Task<int> GetPipelineLogCountAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"SELECT COUNT(*) FROM item_pipeline_log;";

            return Task.Run(() =>
            {
                using var conn = OpenConnection();
                using var stmt = conn.PrepareStatement(sql);
                var rows = stmt.AsRows();
                foreach (var row in rows)
                {
                    return row.GetInt(0);
                }
                return 0;
            }, cancellationToken);
        }

        /// <summary>
        /// Gets resolution log count.
        /// </summary>
        public Task<int> GetResolutionLogCountAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"SELECT COUNT(*) FROM stream_resolution_log;";

            return Task.Run(() =>
            {
                using var conn = OpenConnection();
                using var stmt = conn.PrepareStatement(sql);
                var rows = stmt.AsRows();
                foreach (var row in rows)
                {
                    return row.GetInt(0);
                }
                return 0;
            }, cancellationToken);
        }

        /// <summary>
        /// Prunes pipeline logs before the specified timestamp.
        /// </summary>
        public Task<int> PrunePipelineLogsAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
        {
            const string sql = @"DELETE FROM item_pipeline_log WHERE timestamp < @Before;";

            return Task.Run(() =>
            {
                using var conn = OpenConnection();
                var beforeText = before.ToString("o");
                conn.RunInTransaction(c =>
                {
                    using var stmt = c.PrepareStatement(sql);
                    BindText(stmt, "@Before", beforeText);
                    while (stmt.MoveNext()) { }
                });
                return conn.Changes;
            }, cancellationToken);
        }

        /// <summary>
        /// Prunes resolution logs before the specified timestamp.
        /// </summary>
        public Task<int> PruneResolutionLogsAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
        {
            const string sql = @"DELETE FROM stream_resolution_log WHERE timestamp < @Before;";

            return Task.Run(() =>
            {
                using var conn = OpenConnection();
                var beforeText = before.ToString("o");
                conn.RunInTransaction(c =>
                {
                    using var stmt = c.PrepareStatement(sql);
                    BindText(stmt, "@Before", beforeText);
                    while (stmt.MoveNext()) { }
                });
                return conn.Changes;
            }, cancellationToken);
        }

        // ── Log Query Methods (Sprint 120) ─────────────────────────────────────────

        /// <summary>
        /// Gets pipeline logs with optional filters (for API).
        /// </summary>
        public Task<List<Models.PipelineLogEntry>> GetPipelineLogsAsync(
            string? primaryId,
            string? primaryIdType,
            string? mediaType,
            string? trigger,
            int limit,
            CancellationToken cancellationToken = default)
        {
            var whereClauses = new List<string>();
            if (!string.IsNullOrEmpty(primaryId))
                whereClauses.Add("primary_id = @PrimaryId");
            if (!string.IsNullOrEmpty(primaryIdType))
                whereClauses.Add("primary_id_type = @PrimaryIdType");
            if (!string.IsNullOrEmpty(mediaType))
                whereClauses.Add("media_type = @MediaType");
            if (!string.IsNullOrEmpty(trigger))
                whereClauses.Add("trigger = @Trigger");

            var whereClause = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";
            var sql = $@"
                SELECT primary_id, primary_id_type, media_type, phase, trigger, success, details, timestamp
                FROM item_pipeline_log
                {whereClause}
                ORDER BY timestamp DESC
                LIMIT @Limit;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            if (!string.IsNullOrEmpty(primaryId))
                BindText(stmt, "@PrimaryId", primaryId);
            if (!string.IsNullOrEmpty(primaryIdType))
                BindText(stmt, "@PrimaryIdType", primaryIdType);
            if (!string.IsNullOrEmpty(mediaType))
                BindText(stmt, "@MediaType", mediaType);
            if (!string.IsNullOrEmpty(trigger))
                BindText(stmt, "@Trigger", trigger);
            BindInt(stmt, "@Limit", limit);

            var results = new List<Models.PipelineLogEntry>();
            foreach (var row in stmt.AsRows())
            {
                results.Add(new Models.PipelineLogEntry
                {
                    PrimaryId = row.GetString(0),
                    PrimaryIdType = row.GetString(1),
                    MediaType = row.GetString(2),
                    Phase = row.GetString(3),
                    Trigger = row.GetString(4),
                    Success = row.GetInt(5) == 1,
                    Details = row.IsDBNull(6) ? null : row.GetString(6),
                    Timestamp = DateTimeOffset.Parse(row.GetString(7))
                });
            }
            return Task.FromResult(results);
        }

        /// <summary>
        /// Gets resolution logs with optional filters (for API).
        /// </summary>
        public Task<List<Models.ResolutionLogEntry>> GetResolutionLogsAsync(
            string? primaryId,
            string? primaryIdType,
            string? mediaType,
            int limit,
            CancellationToken cancellationToken = default)
        {
            var whereClauses = new List<string>();
            if (!string.IsNullOrEmpty(primaryId))
                whereClauses.Add("primary_id = @PrimaryId");
            if (!string.IsNullOrEmpty(primaryIdType))
                whereClauses.Add("primary_id_type = @PrimaryIdType");
            if (!string.IsNullOrEmpty(mediaType))
                whereClauses.Add("media_type = @MediaType");

            var whereClause = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";
            var sql = $@"
                SELECT primary_id, primary_id_type, media_type, media_id, stream_count, selected_stream, duration_ms, timestamp
                FROM stream_resolution_log
                {whereClause}
                ORDER BY timestamp DESC
                LIMIT @Limit;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            if (!string.IsNullOrEmpty(primaryId))
                BindText(stmt, "@PrimaryId", primaryId);
            if (!string.IsNullOrEmpty(primaryIdType))
                BindText(stmt, "@PrimaryIdType", primaryIdType);
            if (!string.IsNullOrEmpty(mediaType))
                BindText(stmt, "@MediaType", mediaType);
            BindInt(stmt, "@Limit", limit);

            var results = new List<Models.ResolutionLogEntry>();
            foreach (var row in stmt.AsRows())
            {
                results.Add(new Models.ResolutionLogEntry
                {
                    PrimaryId = row.GetString(0),
                    PrimaryIdType = row.GetString(1),
                    MediaType = row.GetString(2),
                    MediaId = row.GetString(3),
                    StreamCount = row.GetInt(4),
                    SelectedStream = row.IsDBNull(5) ? null : row.GetString(5),
                    DurationMs = row.GetInt(6),
                    Timestamp = DateTimeOffset.Parse(row.GetString(7))
                });
            }
            return Task.FromResult(results);
        }

        /// <summary>
        /// Gets recent logs from both pipeline and resolution tables (for API).
        /// </summary>
        public Task<List<Models.RecentLogEntry>> GetRecentLogsAsync(
            string? level,
            int limit,
            CancellationToken cancellationToken = default)
        {
            // Note: Log level filtering is not implemented in DB schema
            // This is a placeholder implementation
            var sql = @"
                SELECT 'pipeline' as log_type, timestamp, phase, success, details
                FROM item_pipeline_log
                UNION ALL
                SELECT 'resolution' as log_type, timestamp, selected_stream, null as details
                FROM stream_resolution_log
                ORDER BY timestamp DESC
                LIMIT @Limit;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindInt(stmt, "@Limit", limit);

            var results = new List<Models.RecentLogEntry>();
            foreach (var row in stmt.AsRows())
            {
                var logType = row.GetString(0);
                var timestamp = DateTimeOffset.Parse(row.GetString(1));
                var message = logType == "pipeline" ? $"Phase: {row.GetString(2)}, Success: {row.GetInt(3) == 1}" : $"Stream: {row.GetString(2)}";
                var details = row.IsDBNull(3) ? null : row.GetString(3);

                results.Add(new Models.RecentLogEntry
                {
                    Timestamp = timestamp,
                    LogType = logType,
                    Level = "info", // Default level since not stored in DB
                    Message = message,
                    Details = details
                });
            }
            return Task.FromResult(results);
        }
    }
}
