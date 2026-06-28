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
        // ── ingestion_state repository (Sprint 142) ─────────────────────────────

        public async Task<IngestionState?> GetIngestionStateAsync(
            string sourceId,
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT source_id, last_poll_at, last_found_at, watermark
                FROM ingestion_state
                WHERE source_id = @sid;";

            return await QuerySingleAsync(sql, cmd =>
            {
                BindText(cmd, "@sid", sourceId);
            }, row => new IngestionState
            {
                SourceId = row.GetString(0),
                LastPollAt = row.GetString(1),
                LastFoundAt = row.GetString(2),
                Watermark = row.GetString(3),
            });
        }

        public async Task UpsertIngestionStateAsync(
            IngestionState state,
            CancellationToken ct = default)
        {
            const string sql = @"
                INSERT INTO ingestion_state (source_id, last_poll_at, last_found_at, watermark)
                VALUES (@sid, @poll, @found, @watermark)
                ON CONFLICT(source_id) DO UPDATE SET
                    last_poll_at = excluded.last_poll_at,
                    last_found_at = excluded.last_found_at,
                    watermark = excluded.watermark;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@sid", state.SourceId);
                BindText(cmd, "@poll", state.LastPollAt);
                BindText(cmd, "@found", state.LastFoundAt);
                BindText(cmd, "@watermark", state.Watermark);
            }, ct);
        }

        // ── refresh_run_log repository (Sprint 142) ─────────────────────────────

        public Task<long> InsertRunLogAsync(
            string worker,
            string step,
            CancellationToken ct = default)
        {
            const string sql = @"
                INSERT INTO refresh_run_log (run_at, worker, step, status)
                VALUES (datetime('now'), @worker, @step, 'started');";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindText(stmt, "@worker", worker);
            BindText(stmt, "@step", step);
            stmt.MoveNext();
            return Task.FromResult(GetLastInsertRowId(conn));
        }

        public async Task UpdateRunLogAsync(
            long id,
            string status,
            int itemsAffected,
            string? notes,
            CancellationToken ct = default)
        {
            const string sql = @"
                UPDATE refresh_run_log
                SET status = @status,
                    items_affected = @affected,
                    notes = @notes
                WHERE id = @id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@status", status);
                BindInt(cmd, "@affected", itemsAffected);
                BindNullableText(cmd, "@notes", notes);
                BindInt(cmd, "@id", (int)id);
            }, ct);
        }

        public async Task<RefreshRunLog?> GetLatestRunAsync(
            string worker,
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT id, run_at, worker, step, status, items_affected, notes
                FROM refresh_run_log
                WHERE worker = @worker
                ORDER BY id DESC
                LIMIT 1;";

            return await QuerySingleAsync(sql, cmd =>
            {
                BindText(cmd, "@worker", worker);
            }, row => new RefreshRunLog
            {
                Id = row.GetInt64(0),
                RunAt = row.GetString(1),
                Worker = row.GetString(2),
                Step = row.GetString(3),
                Status = row.GetString(4),
                ItemsAffected = row.GetInt(5),
                Notes = row.IsDBNull(6) ? null : row.GetString(6),
            });
        }

        // ── Sprint 142: Enrichment status methods ─────────────────────────────────────

        public async Task UpdateEnrichmentStatusAsync(
            string aioId,
            string source,
            string enrichmentStatus,
            int? retryCount,
            string? nextRetryAt,
            CancellationToken ct = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET enrichment_status = @enrichment_status,
                    retry_count = COALESCE(@retry_count, retry_count + 1),
                    next_retry_at = @next_retry_at
                WHERE aio_id = @aio_id AND source = @source;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@enrichment_status", enrichmentStatus);
                if (retryCount.HasValue)
                    BindInt(cmd, "@retry_count", retryCount.Value);
                else
                    cmd.BindParameters["@retry_count"].BindNull();
                if (nextRetryAt != null)
                    BindText(cmd, "@next_retry_at", nextRetryAt);
                else
                    cmd.BindParameters["@next_retry_at"].BindNull();
                BindText(cmd, "@aio_id", aioId);
                BindText(cmd, "@source", source);
            }, ct);
        }

        public async Task<List<CatalogItem>> GetItemsByEnrichmentStatusAsync(
            string enrichmentStatus,
            int limit,
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE enrichment_status = @enrichment_status
                  AND removed_at IS NULL
                  AND blocked_at IS NULL
                LIMIT @limit;";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@enrichment_status", enrichmentStatus);
                BindInt(cmd, "@limit", limit);
            }, ReadCatalogItem);
        }

        /// <summary>
        /// Sets the enrichment_status for a catalog item by id.
        /// Used by MarvinTask for enrichment retry backoff.
        /// </summary>
        public async Task SetEnrichmentStatusAsync(string itemId, string enrichmentStatus, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET enrichment_status = @enrichment_status, updated_at = datetime('now')
                WHERE id = @id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@enrichment_status", enrichmentStatus);
                BindText(cmd, "@id", itemId);
            }, cancellationToken);
        }

        /// <summary>
        /// Updates retry_count and next_retry_at for a catalog item by id.
        /// Used by MarvinTask for enrichment retry backoff.
        /// </summary>
        public async Task UpdateItemRetryInfoAsync(
            string itemId,
            int retryCount,
            long? nextRetryAt,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET retry_count = @retry_count,
                    next_retry_at = @next_retry_at,
                    updated_at = datetime('now')
                WHERE id = @id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindInt(cmd, "@retry_count", retryCount);
                if (nextRetryAt.HasValue)
                    BindInt(cmd, "@next_retry_at", (int)nextRetryAt.Value);
                else
                    cmd.BindParameters["@next_retry_at"].BindNull();
                BindText(cmd, "@id", itemId);
            }, cancellationToken);
        }

        /// <summary>
        /// Returns all catalog items with blocked_at IS NOT NULL (admin-blocked tombstones).
        /// Used by AdminService for the Blocked Items tab.
        /// </summary>
        public async Task<List<CatalogItem>> GetBlockedItemsAsync(CancellationToken ct = default)
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE blocked_at IS NOT NULL
                ORDER BY blocked_at DESC;";

            return await QueryListAsync(sql, null, ReadCatalogItem);
        }

        /// <summary>
        /// Clears the blocked_at/blocked_by tombstone and resets the item to NeedsEnrich.
        /// Used by AdminService Unblock action.
        /// </summary>
        public async Task UnblockItemAsync(string itemId, CancellationToken ct = default)
        {
            const string sql = @"
                UPDATE catalog_items
                SET blocked_at  = NULL,
                    blocked_by  = NULL,
                    enrichment_status  = 'NeedsEnrich',
                    retry_count = 0,
                    next_retry_at = NULL,
                    updated_at  = datetime('now')
                WHERE id = @id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id", itemId);
            }, ct);

            _logger.LogInformation("[DatabaseManager] Unblocked item {Id}", itemId);
        }

        /// <summary>
        /// Returns the set of IMDB IDs the user has pinned (via any pin source).
        /// Used by DiscoverService to compute per-user InLibrary status.
        /// </summary>
        /// <summary>
        /// Returns catalog items by a list of IDs.
        /// </summary>
        public async Task<List<CatalogItem>> GetCatalogItemsByIdsAsync(List<string> ids, CancellationToken ct = default)
        {
            if (ids == null || ids.Count == 0)
                return new List<CatalogItem>();

            var placeholders = string.Join(",", ids.Select((_, i) => $"@id{i}"));
            var sql = $@"
                SELECT * FROM catalog_items
                WHERE id IN ({placeholders});";

            return await QueryListAsync(sql, cmd =>
            {
                for (var i = 0; i < ids.Count; i++)
                    BindText(cmd, $"@id{i}", ids[i]);
            }, ReadCatalogItem);
        }

        /// <summary>
        /// Queries for a scalar integer value (COUNT, SUM, etc.).
        /// Used by MarvinTask for counting Blocked and NeedsEnrich items.
        /// </summary>
        public Task<int> QueryScalarIntAsync(string sql, CancellationToken cancellationToken = default)
        {
            return QueryScalarIntAsync(sql, null, cancellationToken);
        }

        /// <summary>
        /// Queries for a scalar integer value with parameter binding.
        /// </summary>
        public Task<int> QueryScalarIntAsync(string sql, Action<IStatement>? bindParams, CancellationToken cancellationToken = default)
        {
            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            bindParams?.Invoke(stmt);

            foreach (var row in stmt.AsRows())
                return Task.FromResult(row.IsDBNull(0) ? 0 : row.GetInt(0));

            return Task.FromResult(0);
        }

        // ── playback_log repository ─────────────────────────────────────────────

        /// <summary>
        /// Appends a playback event to the log.
        /// Automatically prunes the table when it exceeds <c>500</c> rows.
        /// </summary>
        public async Task LogPlaybackAsync(PlaybackEntry entry, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO playback_log
                    (id, aio_id, title, season, episode,
                     resolution_mode, quality_served, client_type,
                     latency_ms, bitrate_sustained, quality_downgrade,
                     error_message, played_at)
                VALUES
                    (@id, @aio_id, @title, @season, @episode,
                     @resolution_mode, @quality_served, @client_type,
                     @latency_ms, @bitrate_sustained, @quality_downgrade,
                     @error_message, @played_at);";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id",               entry.Id);
                BindText(cmd, "@aio_id",          entry.AioId);
                BindNullableText(cmd, "@title",           entry.Title);
                BindNullableInt(cmd,  "@season",          entry.Season);
                BindNullableInt(cmd,  "@episode",         entry.Episode);
                BindText(cmd, "@resolution_mode",  entry.ResolutionMode);
                BindNullableText(cmd, "@quality_served",  entry.QualityServed);
                BindNullableText(cmd, "@client_type",     entry.ClientType);
                BindNullableInt(cmd,  "@latency_ms",      entry.LatencyMs);
                BindNullableInt(cmd,  "@bitrate_sustained",entry.BitrateSustained);
                cmd.BindParameters["@quality_downgrade"].Bind(entry.QualityDowngrade);
                BindNullableText(cmd, "@error_message",   entry.ErrorMessage);
                BindText(cmd, "@played_at",        entry.PlayedAt);
            }, cancellationToken);

            await PrunePlaybackLogAsync(PlaybackLogMaxRows);
        }

        /// <summary>
        /// Returns the most recent <paramref name="limit"/> playback log entries.
        /// </summary>
        public async Task<List<PlaybackEntry>> GetRecentPlaybackAsync(int limit)
        {
            const string sql = @"
                SELECT id, aio_id, title, season, episode,
                       resolution_mode, quality_served, client_type,
                       latency_ms, bitrate_sustained, quality_downgrade,
                       error_message, played_at
                FROM playback_log
                ORDER BY played_at DESC
                LIMIT @limit;";

            return await QueryListAsync(sql,
                cmd => cmd.BindParameters["@limit"].Bind(limit),
                ReadPlaybackEntry);
        }

        /// <summary>
        /// Deletes oldest rows when the table exceeds <paramref name="maxRows"/>.
        /// </summary>
        public async Task PrunePlaybackLogAsync(int maxRows, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                DELETE FROM playback_log
                WHERE id IN (
                    SELECT id FROM playback_log
                    ORDER BY played_at DESC
                    LIMIT -1 OFFSET @max_rows
                );";

            await ExecuteWriteAsync(sql,
                cmd => cmd.BindParameters["@max_rows"].Bind(maxRows), cancellationToken);
        }

        // ── api_budget repository ───────────────────────────────────────────────

        /// <summary>
        /// Increments today's API call counter by one.
        /// Auto-creates the row for today if it doesn't exist.
        /// </summary>
        public async Task IncrementApiCallCountAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO api_budget (date, calls_made, calls_budget)
                VALUES (date('now'), 1, @budget)
                ON CONFLICT(date) DO UPDATE SET
                    calls_made = api_budget.calls_made + 1;";

            var budget = Plugin.Instance?.Configuration?.ApiDailyBudget ?? 2000;
            await ExecuteWriteAsync(sql,
                cmd => cmd.BindParameters["@budget"].Bind(budget), cancellationToken);
        }

        /// <summary>
        /// Returns <c>true</c> if today's call count has reached or exceeded the
        /// configured daily budget, or if the API is currently in a back-off window.
        /// </summary>
        public Task<bool> IsBudgetExhaustedAsync()
        {
            const string sql = @"
                SELECT calls_made, calls_budget, backoff_until
                FROM api_budget
                WHERE date = date('now')
                LIMIT 1;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            foreach (var row in stmt.AsRows())
            {
                var callsMade    = row.GetInt(0);
                var callsBudget  = row.GetInt(1);
                var backoffUntil = row.IsDBNull(2) ? null : row.GetString(2);

                if (callsMade >= callsBudget)
                    return Task.FromResult(true);

                if (!string.IsNullOrEmpty(backoffUntil)
                    && DateTime.TryParse(backoffUntil, out var backoffDt)
                    && DateTime.UtcNow < backoffDt)
                    return Task.FromResult(true);

                return Task.FromResult(false);
            }

            return Task.FromResult(false); // No row today → fresh budget
        }

        /// <summary>
        /// Records a 429 response and sets a back-off window.
        /// </summary>
        /// <param name="backoffUntil">ISO-8601 UTC timestamp to back off until.</param>
        public async Task RecordRateLimitHitAsync(string backoffUntil, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO api_budget (date, calls_made, calls_budget, last_429_at, backoff_until)
                VALUES (date('now'), 0, @budget, datetime('now'), @backoff_until)
                ON CONFLICT(date) DO UPDATE SET
                    last_429_at  = datetime('now'),
                    backoff_until = @backoff_until;";

            var budget = Plugin.Instance?.Configuration?.ApiDailyBudget ?? 2000;
            await ExecuteWriteAsync(sql, cmd =>
            {
                cmd.BindParameters["@budget"].Bind(budget);
                BindText(cmd, "@backoff_until", backoffUntil);
            }, cancellationToken);
        }

        // ── sync_state repository ───────────────────────────────────────────────

        /// <summary>
        /// Returns the sync state for the given source key, or null if never synced.
        /// </summary>
        public async Task<SyncState?> GetSyncStateAsync(string sourceKey)
        {
            const string sql = @"
                SELECT source_key, last_sync_at, last_etag, last_cursor, item_count, status,
                       consecutive_failures, last_error, last_reached_at,
                       catalog_name, catalog_type, items_target, items_running
                FROM sync_state
                WHERE source_key = @source_key;";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@source_key", sourceKey),
                ReadSyncState);
        }

        /// <summary>
        /// Inserts or replaces the full sync state for a source.
        /// Prefer <see cref="RecordSyncSuccessAsync"/> and
        /// <see cref="RecordSyncFailureAsync"/> for routine health recording.
        /// </summary>
        public async Task UpsertSyncStateAsync(SyncState state, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO sync_state
                    (source_key, last_sync_at, last_etag, last_cursor, item_count, status,
                     consecutive_failures, last_error, last_reached_at, updated_at)
                VALUES
                    (@source_key, @last_sync_at, @last_etag, @last_cursor, @item_count, @status,
                     @consecutive_failures, @last_error, @last_reached_at, datetime('now'))
                ON CONFLICT(source_key) DO UPDATE SET
                    last_sync_at         = excluded.last_sync_at,
                    last_etag            = excluded.last_etag,
                    last_cursor          = excluded.last_cursor,
                    item_count           = excluded.item_count,
                    status               = excluded.status,
                    consecutive_failures = excluded.consecutive_failures,
                    last_error           = excluded.last_error,
                    last_reached_at      = excluded.last_reached_at,
                    updated_at           = datetime('now');";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@source_key",   state.SourceKey);
                BindNullableText(cmd, "@last_sync_at",         state.LastSyncAt);
                BindNullableText(cmd, "@last_etag",            state.LastEtag);
                BindNullableText(cmd, "@last_cursor",          state.LastCursor);
                cmd.BindParameters["@item_count"].Bind(state.ItemCount);
                BindText(cmd, "@status",                       state.Status);
                cmd.BindParameters["@consecutive_failures"].Bind(state.ConsecutiveFailures);
                BindNullableText(cmd, "@last_error",           state.LastError);
                BindNullableText(cmd, "@last_reached_at",      state.LastReachedAt);
            }, cancellationToken);
        }

        /// <summary>
        /// Records a successful sync for a source.
        /// Resets <c>consecutive_failures</c> to 0, clears <c>last_error</c>,
        /// updates <c>last_sync_at</c> and <c>last_reached_at</c>, sets status = <c>ok</c>.
        /// </summary>
        public async Task RecordSyncSuccessAsync(string sourceKey, int itemCount, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO sync_state
                    (source_key, last_sync_at, last_reached_at, item_count, status,
                     consecutive_failures, last_error, items_running, updated_at)
                VALUES
                    (@source_key, datetime('now'), datetime('now'), @item_count, 'ok', 0, NULL, @item_count, datetime('now'))
                ON CONFLICT(source_key) DO UPDATE SET
                    last_sync_at         = datetime('now'),
                    last_reached_at      = datetime('now'),
                    item_count           = @item_count,
                    status               = 'ok',
                    consecutive_failures = 0,
                    last_error           = NULL,
                    items_running        = @item_count,
                    updated_at           = datetime('now');";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@source_key", sourceKey);
                cmd.BindParameters["@item_count"].Bind(itemCount);
            }, cancellationToken);
        }

        /// <summary>
        /// Records a failed sync attempt.
        /// Increments <c>consecutive_failures</c> and sets status to
        /// <c>warn</c> (1–2 failures) or <c>error</c> (≥ 3).
        /// Does <em>not</em> update <c>last_sync_at</c> or <c>item_count</c>
        /// — those reflect the last <em>successful</em> run only.
        /// </summary>
        public async Task RecordSyncFailureAsync(string sourceKey, string errorMessage, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO sync_state
                    (source_key, status, consecutive_failures, last_error)
                VALUES
                    (@source_key, 'warn', 1, @error)
                ON CONFLICT(source_key) DO UPDATE SET
                    consecutive_failures = sync_state.consecutive_failures + 1,
                    last_error           = @error,
                    status               = CASE
                        WHEN sync_state.consecutive_failures + 1 >= 3 THEN 'error'
                        ELSE 'warn'
                    END;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@source_key", sourceKey);
                BindText(cmd, "@error",      errorMessage);
            }, cancellationToken);
        }

        /// <summary>
        /// Marks a catalog as actively syncing and records the target item count.
        /// Called before each catalog page-fetch begins so the dashboard can show
        /// a live "running" indicator immediately.
        /// </summary>
        public async Task RecordCatalogRunningAsync(
            string sourceKey,
            string catalogName,
            string catalogType,
            int    itemsTarget,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
        INSERT INTO sync_state
            (source_key, catalog_name, catalog_type, items_target, items_running, status)
        VALUES
            (@source_key, @catalog_name, @catalog_type, @items_target, 0, 'running')
        ON CONFLICT(source_key) DO UPDATE SET
            catalog_name  = @catalog_name,
            catalog_type  = @catalog_type,
            items_target  = @items_target,
            items_running = 0,
            status        = 'running';";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@source_key",    sourceKey);
                BindText(cmd, "@catalog_name",  catalogName);
                BindText(cmd, "@catalog_type",  catalogType);
                cmd.BindParameters["@items_target"].Bind(itemsTarget);
            }, cancellationToken);
        }

        /// <summary>
        /// Updates the live item count for an actively-syncing catalog.
        /// Called after each fetched page.
        /// </summary>
        public async Task UpdateCatalogProgressAsync(string sourceKey, int itemsRunning, CancellationToken cancellationToken = default)
        {
            const string sql = @"
        INSERT INTO sync_state (source_key, items_running)
        VALUES (@source_key, @items_running)
        ON CONFLICT(source_key) DO UPDATE SET
            items_running = @items_running;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@source_key",   sourceKey);
                cmd.BindParameters["@items_running"].Bind(itemsRunning);
            }, cancellationToken);
        }

        /// <summary>
        /// Returns all sync state rows ordered by source key.
        /// Used by the health dashboard to show all provider and per-catalog statuses.
        /// </summary>
        public async Task<List<SyncState>> GetAllSyncStatesAsync()
        {
            const string sql = @"
                SELECT source_key, last_sync_at, last_etag, last_cursor, item_count, status,
                       consecutive_failures, last_error, last_reached_at,
                       catalog_name, catalog_type, items_target, items_running
                FROM sync_state
                ORDER BY source_key;";

            return await QueryListAsync(sql, null, ReadSyncState);
        }

        /// <summary>
        /// Clears all sync states so the next sync will run immediately.
        /// Used to force a re-sync after configuration changes.
        /// </summary>
        public async Task ClearAllSyncStatesAsync(CancellationToken cancellationToken = default)
        {
            await ExecuteWriteAsync("DELETE FROM sync_state;", _ => { }, cancellationToken);
            _logger.LogInformation("[DatabaseManager] All sync states cleared");
        }

        // ── collection_membership repository ───────────────────────────────────────────
        // Sprint 100C-01: Collection membership recording.

        /// <summary>
        /// Records that an item belongs to a collection.
        /// Upserts on (collection_name, emby_item_id).
        /// Sprint 100C-01: Collection membership recording.
        /// </summary>
        public async Task UpsertCollectionMembershipAsync(
            string collectionName,
            string embyItemId,
            string source,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                INSERT INTO collection_membership
                    (collection_name, emby_item_id, source, last_seen)
                VALUES (@collection_name, @emby_item_id, @source, datetime('now'))
                ON CONFLICT(collection_name, emby_item_id)
                DO UPDATE SET source = @source, last_seen = datetime('now');";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@collection_name", collectionName);
                BindText(cmd, "@emby_item_id", embyItemId);
                BindText(cmd, "@source", source);
            }, cancellationToken);
        }

        /// <summary>
        /// Returns all distinct collection names with their item counts.
        /// Used by CollectionSyncTask to iterate over collections.
        /// Sprint 100C-02: Collection sync task.
        /// </summary>
        public async Task<Dictionary<string, int>> GetAllCollectionsAsync(CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT collection_name, COUNT(*) as item_count
                FROM collection_membership
                GROUP BY collection_name
                ORDER BY collection_name;";

            var results = await QueryListAsync(sql, null, row =>
                new
                {
                    Name = row.GetString(0),
                    Count = row.GetInt(1)
                });

            return results.ToDictionary(r => r.Name, r => r.Count);
        }

        /// <summary>
        /// Returns all Emby item IDs that belong to a collection.
        /// Used by CollectionSyncTask to populate BoxSet members.
        /// Sprint 100C-02: Collection sync task.
        /// </summary>
        public async Task<List<string>> GetCollectionMembersAsync(
            string collectionName,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT emby_item_id
                FROM collection_membership
                WHERE collection_name = @collection_name
                ORDER BY last_seen DESC;";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@collection_name", collectionName);
            }, row => row.GetString(0));
        }

        /// <summary>
        /// Removes items from collection_membership that are no longer in the collection.
        /// Used by CollectionSyncTask after orphan deletion.
        /// Sprint 100C-02: Collection sync task.
        /// </summary>
        public async Task RemoveCollectionMembersAsync(
            string collectionName,
            List<string> itemIds,
            CancellationToken cancellationToken = default)
        {
            if (itemIds.Count == 0)
                return;

            const string sql = @"
                DELETE FROM collection_membership
                WHERE collection_name = @collection_name
                  AND emby_item_id IN ({placeholders});";

            var placeholders = string.Join(",", itemIds.Select(_ => "?"));
            var finalSql = sql.Replace("{placeholders}", placeholders);

            await ExecuteWriteAsync(finalSql, cmd =>
            {
                BindText(cmd, "@collection_name", collectionName);
                int idx = 1;
                foreach (var itemId in itemIds)
                {
                    BindText(cmd, "@item" + idx, itemId);
                    idx++;
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Clears all collection memberships for a source.
        /// Used when re-syncing catalogs.
        /// </summary>
        public async Task ClearCollectionMembershipsBySourceAsync(
            string source,
            CancellationToken cancellationToken = default)
        {
            await ExecuteWriteAsync(
                "DELETE FROM collection_membership WHERE source = @source;",
                cmd => BindText(cmd, "@source", source),
                cancellationToken);
        }

        // ── Night 3 helpers: resolution queue, stats ────────────────────────────

        /// <summary>
        /// Marks existing entries as stale for re-resolution.
        /// </summary>
        public async Task QueueForResolutionAsync(
            string aioId, int? season, int? episode, string tier, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE stream_resolution_cache
                SET status     = 'stale',
                    expires_at = datetime('now'),
                    updated_at = datetime('now')
                WHERE aio_id = @aio_id
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
        /// Returns the number of active (non-removed) catalog items.
        /// </summary>
        public Task<int> GetCatalogItemCountAsync()
        {
            const string sql = @"
                SELECT COUNT(*) FROM catalog_items WHERE removed_at IS NULL;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            foreach (var row in stmt.AsRows())
                return Task.FromResult(row.IsDBNull(0) ? 0 : row.GetInt(0));
            return Task.FromResult(0);
        }

        /// <summary>
        /// Returns the count of catalog items in the given item state.
        /// Used by the Marvin dashboard to display state distribution.
        /// </summary>
        public Task<int> GetCatalogItemCountByItemStateAsync(ItemState state)
        {
            const string sql = @"
                SELECT COUNT(*) FROM catalog_items
                WHERE item_state = @state AND removed_at IS NULL;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            stmt.BindParameters["@state"].Bind((int)state);
            foreach (var row in stmt.AsRows())
                return Task.FromResult(row.IsDBNull(0) ? 0 : row.GetInt(0));
            return Task.FromResult(0);
        }

        /// <summary>
        /// Full-text search over catalog item titles (case-insensitive, prefix-friendly).
        /// Returns up to <paramref name="limit"/> active items ordered by title.
        /// </summary>
        public async Task<List<CatalogItem>> SearchCatalogAsync(string query, int limit = 20)
        {
            const string sql = @"
                SELECT * FROM catalog_items
                WHERE removed_at IS NULL
                  AND title LIKE @q ESCAPE '\'
                ORDER BY title
                LIMIT @limit;";

            // Escape % and _ in the user query so they're treated as literals,
            // then wrap with % for substring match.
            var escaped = query
                .Replace(@"\", @"\\")
                .Replace("%", @"\%")
                .Replace("_", @"\_");
            var pattern = $"%{escaped}%";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@q",     pattern);
                cmd.BindParameters["@limit"].Bind(limit);
            }, ReadCatalogItem);
        }

        /// <summary>
        /// Returns per-source item counts for the health dashboard.
        /// Key = source key (e.g. <c>aiostreams</c>, <c>trakt:username</c>);
        /// Value = number of active (non-removed) catalog items.
        /// </summary>
        public Task<Dictionary<string, int>> GetCatalogCountsBySourceAsync()
        {
            const string sql = @"
                SELECT source, COUNT(*) AS cnt
                FROM catalog_items
                WHERE removed_at IS NULL
                GROUP BY source
                ORDER BY cnt DESC;";

            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            foreach (var row in stmt.AsRows())
            {
                var sourceKey = row.IsDBNull(0) ? "unknown" : row.GetString(0);
                var cnt       = row.IsDBNull(1) ? 0 : row.GetInt(1);
                result[sourceKey] = cnt;
            }

            return Task.FromResult(result);
        }

        /// <summary>
        /// Returns a simple stats snapshot of the resolution_cache table for the
        /// health dashboard: total rows, valid rows, stale rows, failed rows.
        /// </summary>
        public Task<ResolutionCacheStats> GetResolutionCacheStatsAsync()
        {
            const string sql = @"
                SELECT
                    COUNT(*)                                                          AS total,
                    SUM(CASE WHEN status='valid'  AND expires_at > datetime('now')
                             THEN 1 ELSE 0 END)                                       AS valid_unexpired,
                    SUM(CASE WHEN status='stale'
                              OR (status='valid' AND expires_at <= datetime('now'))
                             THEN 1 ELSE 0 END)                                       AS stale,
                    SUM(CASE WHEN status='failed' THEN 1 ELSE 0 END)                  AS failed
                FROM stream_resolution_cache;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            foreach (var row in stmt.AsRows())
            {
                return Task.FromResult(new ResolutionCacheStats
                {
                    Total          = row.IsDBNull(0) ? 0 : row.GetInt(0),
                    ValidUnexpired = row.IsDBNull(1) ? 0 : row.GetInt(1),
                    Stale          = row.IsDBNull(2) ? 0 : row.GetInt(2),
                    Failed         = row.IsDBNull(3) ? 0 : row.GetInt(3),
                });
            }

            return Task.FromResult(new ResolutionCacheStats());
        }

        /// <summary>
        /// Returns a per-item resolution coverage breakdown for the dashboard.
        ///
        /// Only counts items with <c>local_source='strm'</c> because those are the
        /// items that actually need a cached stream URL at playback time.
        /// Library-tracked items play from their real file path — no resolution needed.
        /// </summary>
        public Task<ResolutionCoverageStats> GetResolutionCoverageAsync()
        {
            // Three correlated subqueries — fast enough for catalogs up to ~5 000 items.
            const string totalSql = @"
                SELECT COUNT(DISTINCT aio_id)
                FROM catalog_items
                WHERE removed_at IS NULL AND local_source = 'strm';";

            const string validSql = @"
                SELECT COUNT(DISTINCT ci.aio_id)
                FROM catalog_items ci
                WHERE ci.removed_at IS NULL AND ci.local_source = 'strm'
                  AND EXISTS (
                      SELECT 1 FROM stream_resolution_cache rc
                      WHERE rc.aio_id    = ci.aio_id
                        AND rc.status     = 'valid'
                        AND rc.expires_at > datetime('now')
                  );";

            const string uncachedSql = @"
                SELECT COUNT(DISTINCT ci.aio_id)
                FROM catalog_items ci
                WHERE ci.removed_at IS NULL AND ci.local_source = 'strm'
                  AND NOT EXISTS (
                      SELECT 1 FROM stream_resolution_cache rc
                      WHERE rc.aio_id = ci.aio_id
                  );";

            static int RunScalar(IDatabaseConnection conn, string sql)
            {
                using var stmt = conn.PrepareStatement(sql);
                foreach (var row in stmt.AsRows())
                    return row.IsDBNull(0) ? 0 : row.GetInt(0);
                return 0;
            }

            using var conn = OpenConnection();
            var total    = RunScalar(conn, totalSql);
            var valid    = RunScalar(conn, validSql);
            var uncached = RunScalar(conn, uncachedSql);

            return Task.FromResult(new ResolutionCoverageStats
            {
                TotalStrm   = total,
                ValidCached = valid,
                StaleCached = Math.Max(0, total - valid - uncached),
                Uncached    = uncached,
            });
        }

        /// <summary>
        /// Returns today's API budget usage (calls made + budget limit).
        /// Returns zeros if no row exists for today.
        /// </summary>
        public Task<(int CallsMade, int CallsBudget)> GetApiBudgetTodayAsync()
        {
            const string sql = @"
                SELECT calls_made, calls_budget
                FROM api_budget
                WHERE date = date('now')
                LIMIT 1;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            foreach (var row in stmt.AsRows())
                return Task.FromResult((row.GetInt(0), row.GetInt(1)));

            return Task.FromResult((0, Plugin.Instance?.Configuration?.ApiDailyBudget ?? 2000));
        }

        /// <summary>
        /// U1: Returns catalog items that are currently stuck in a failed resolution state
        /// (status='failed', TTL not yet expired) so the health dashboard can surface them
        /// as "unavailable".  Joined to catalog_items to supply title and media type.
        /// Capped at <paramref name="limit"/> rows ordered by most recently failed first.
        /// </summary>
        public Task<List<(string AioId, string Title, int? Season, int? Episode, string ExpiresAt)>>
            GetFailedItemsAsync(int limit = 50)
        {
            const string sql = @"
                SELECT rc.aio_id, COALESCE(ci.title, rc.aio_id), rc.season, rc.episode, rc.expires_at
                FROM stream_resolution_cache rc
                LEFT JOIN catalog_items ci
                       ON ci.aio_id = rc.aio_id AND ci.removed_at IS NULL
                WHERE rc.status = 'failed'
                  AND rc.expires_at > datetime('now')
                ORDER BY rc.expires_at DESC
                LIMIT @limit;";

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            BindNullableInt(stmt, "@limit", limit);

            var rows = new List<(string, string, int?, int?, string)>();
            foreach (var row in stmt.AsRows())
            {
                rows.Add((
                    row.GetString(0),
                    row.GetString(1),
                    row.IsDBNull(2) ? (int?)null : row.GetInt(2),
                    row.IsDBNull(3) ? (int?)null : row.GetInt(3),
                    row.IsDBNull(4) ? string.Empty : row.GetString(4)
                ));
            }
            return Task.FromResult(rows);
        }

        // ── collection_membership repository ────────────────────────────────────────

        /// <summary>
        /// Bulk upsert of collection membership rows.
        /// ON CONFLICT updates last_seen and updated_at.
        /// </summary>
        public async Task UpsertCollectionMembershipBatchAsync(
            List<(string CollectionName, string? EmbyItemId, string AioId, string Source, string? UserId)> batch,
            CancellationToken ct = default)
        {
            if (batch.Count == 0) return;

            var now = DateTime.UtcNow.ToString("o");
            const string sql = @"
                INSERT INTO collection_membership (collection_name, emby_item_id, aio_id, source, user_id, last_seen, created_at, updated_at)
                VALUES (@cn, @eii, @aid, @src, @uid, @now, @now, @now)
                ON CONFLICT(collection_name, aio_id, user_id) DO UPDATE SET
                    emby_item_id = COALESCE(excluded.emby_item_id, collection_membership.emby_item_id),
                    last_seen = excluded.last_seen,
                    updated_at = excluded.updated_at;";

            await ExecuteWriteAsync(sql, stmt =>
            {
                foreach (var item in batch)
                {
                    BindText(stmt, "@cn", item.CollectionName);
                    BindNullableText(stmt, "@eii", item.EmbyItemId);
                    BindText(stmt, "@aid", item.AioId);
                    BindText(stmt, "@src", item.Source);
                    BindNullableText(stmt, "@uid", item.UserId);
                    BindText(stmt, "@now", now);
                    while (stmt.MoveNext()) { }
                    stmt.Reset();
                }
            }, ct);
        }

        /// <summary>
        /// Returns collection membership rows where emby_item_id IS NULL (pending resolution).
        /// </summary>
        public async Task<List<(int Id, string CollectionName, string AioId, string Source, string? UserId)>> GetPendingCollectionMembershipsAsync(
            CancellationToken ct = default)
        {
            const string sql = @"
                SELECT id, collection_name, aio_id, source, user_id
                FROM collection_membership
                WHERE emby_item_id IS NULL;";

            return await QueryListAsync(sql, null, r => (
                r.GetInt(0),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4)
            ));
        }

        /// <summary>
        /// Sets emby_item_id on a collection membership row.
        /// </summary>
        public async Task UpdateCollectionMembershipEmbyItemIdAsync(
            int id, string embyItemId, CancellationToken ct = default)
        {
            const string sql = @"
                UPDATE collection_membership
                SET emby_item_id = @eii, updated_at = @now
                WHERE id = @id;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@eii", embyItemId);
                BindInt(cmd, "@id", id);
                BindText(cmd, "@now", DateTime.UtcNow.ToString("o"));
            }, ct);
        }

        /// <summary>
        /// Returns true if the user has a collection_membership row for the item
        /// (pending OR resolved). Used by RemoveFromLibrary to report accurately.
        /// </summary>
        public async Task<bool> CollectionMembershipExistsAsync(
            string aioId, string userId, CancellationToken ct = default)
        {
            const string sql = "SELECT COUNT(*) FROM collection_membership WHERE aio_id = @aid AND user_id = @uid;";
            return await QueryScalarIntAsync(sql, cmd =>
            {
                BindText(cmd, "@aid", aioId);
                BindText(cmd, "@uid", userId);
            }) > 0;
        }

        /// <summary>
        /// Removes a specific collection membership row by aio_id and optional user_id.
        /// </summary>
        public async Task RemoveCollectionMembershipAsync(
            string aioId, string? userId, CancellationToken ct = default)
        {
            if (userId != null)
            {
                const string sql = @"
                    DELETE FROM collection_membership
                    WHERE aio_id = @aid AND user_id = @uid;";

                await ExecuteWriteAsync(sql, cmd =>
                {
                    BindText(cmd, "@aid", aioId);
                    BindText(cmd, "@uid", userId);
                }, ct);
            }
            else
            {
                const string sql = @"
                    DELETE FROM collection_membership
                    WHERE aio_id = @aid AND user_id IS NULL;";

                await ExecuteWriteAsync(sql, cmd =>
                {
                    BindText(cmd, "@aid", aioId);
                }, ct);
            }
        }

        /// <summary>
        /// Returns true if an item has any collection membership row (protected from pruning).
        /// </summary>
        public async Task<bool> HasCollectionMembershipAsync(string aioId, CancellationToken ct = default)
        {
            const string sql = @"
                SELECT COUNT(*) FROM collection_membership
                WHERE aio_id = @aid LIMIT 1;";

            return await QueryScalarIntAsync(sql, cmd => BindText(cmd, "@aid", aioId)) > 0;
        }

        /// <summary>
        /// Updates last_successful_sync for a collection identified by source_id.
        /// Called on successful sync.
        /// </summary>
        public async Task UpdateCollectionLastSuccessfulSyncAsync(
            string sourceId, long timestamp, CancellationToken ct = default)
        {
            const string sql = @"
                UPDATE collections
                SET last_successful_sync = @ts, updated_at = @now
                WHERE source_id = @sid;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindInt(cmd, "@ts", (int)timestamp);
                BindText(cmd, "@sid", sourceId);
                BindText(cmd, "@now", DateTime.UtcNow.ToString("o"));
            }, ct);
        }
    }
}
