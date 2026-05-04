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
        // ── resolution_cache repository ─────────────────────────────────────────

        // ── stream_resolution_cache repository (consolidated) ────────────────────

        // Shared SQL constants for the consolidated stream_resolution_cache table

        private const string CandidateDeleteSql = @"
            DELETE FROM stream_resolution_cache
            WHERE aio_id = @aio_id
              AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
              AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL));";

        private const string CandidateInsertSql = @"
            INSERT INTO stream_resolution_cache
                (id, aio_id, imdb_id, season, episode, rank,
                 provider_key, url, headers_json,
                 quality_tier, file_name, file_size, bitrate_kbps,
                 is_cached, resolved_at, expires_at, status,
                 info_hash, file_idx, stream_key, binge_group,
                 languages, subtitles_json, description,
                 raw_stream_json)
            VALUES
                (@id, @aio_id, @imdb_id, @season, @episode, @rank,
                 @provider_key, @url, @headers_json,
                 @quality_tier, @file_name, @file_size, @bitrate_kbps,
                 @is_cached, @resolved_at, @expires_at, @status,
                 @info_hash, @file_idx, @stream_key, @binge_group,
                 @languages, @subtitles_json, @description,
                 @raw_stream_json);";

        /// <summary>
        /// Replaces all stream_candidates rows for the item identified by the first
        /// candidate's (imdb_id, season, episode).  Must be called inside a transaction.
        /// </summary>
        private void InsertCandidatesCore(IDatabaseConnection c, List<StreamCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0) return;

            var first = candidates[0];
            using (var delStmt = c.PrepareStatement(CandidateDeleteSql))
            {
                BindText(delStmt,        "@aio_id",  first.ImdbId);
                BindNullableInt(delStmt, "@season",  first.Season);
                BindNullableInt(delStmt, "@episode", first.Episode);
                while (delStmt.MoveNext()) { }
            }

            foreach (var cand in candidates)
            {
                using var insStmt = c.PrepareStatement(CandidateInsertSql);
                BindText(insStmt,         "@id",           cand.Id);
                BindText(insStmt,         "@aio_id",       cand.ImdbId);
                BindNullableText(insStmt, "@imdb_id",      cand.ImdbId.StartsWith("tt", StringComparison.Ordinal) ? cand.ImdbId : null);
                BindNullableInt(insStmt,  "@season",       cand.Season);
                BindNullableInt(insStmt,  "@episode",      cand.Episode);
                insStmt.BindParameters["@rank"].Bind(cand.Rank);
                BindText(insStmt,         "@provider_key", cand.ProviderKey);
                BindText(insStmt,         "@url",          cand.Url);
                BindNullableText(insStmt, "@headers_json", cand.HeadersJson);
                BindNullableText(insStmt, "@quality_tier", cand.QualityTier);
                BindNullableText(insStmt, "@file_name",    cand.FileName);
                BindNullableLong(insStmt, "@file_size",    cand.FileSize);
                BindNullableInt(insStmt,  "@bitrate_kbps", cand.BitrateKbps);
                insStmt.BindParameters["@is_cached"].Bind(cand.IsCached ? 1 : 0);
                BindText(insStmt,         "@resolved_at",    cand.ResolvedAt);
                BindText(insStmt,         "@expires_at",     cand.ExpiresAt);
                BindText(insStmt,         "@status",         cand.Status);
                BindNullableText(insStmt, "@info_hash",      cand.InfoHash);
                BindNullableInt(insStmt,  "@file_idx",       cand.FileIdx);
                BindNullableText(insStmt, "@stream_key",     cand.StreamKey);
                BindNullableText(insStmt, "@binge_group",    cand.BingeGroup);
                BindNullableText(insStmt, "@languages",      cand.Languages);
                BindNullableText(insStmt, "@subtitles_json", cand.SubtitlesJson);
                BindNullableText(insStmt, "@description",    cand.Description);
                BindNullableText(insStmt, "@raw_stream_json", cand.RawStreamJson);
                while (insStmt.MoveNext()) { }
            }
        }

        /// <summary>
        /// Atomically replaces all candidate rows for a given (imdb_id, season, episode)
        /// with the supplied list.  Runs as a single transaction: delete then bulk-insert.
        /// A no-op when <paramref name="candidates"/> is empty.
        /// </summary>
        public async Task UpsertStreamCandidatesAsync(List<StreamCandidate> candidates, CancellationToken cancellationToken = default)
        {
            if (candidates == null || candidates.Count == 0) return;

            await _dbWriteGate.WaitAsync(cancellationToken);
            try
            {
                using var conn = OpenConnection();
                conn.RunInTransaction(c => InsertCandidatesCore(c, candidates));
            }
            finally
            {
                _dbWriteGate.Release();
            }
        }

        /// <summary>
        /// Atomically writes ranked candidate list into stream_resolution_cache
        /// in a single SQLite transaction.
        /// </summary>
        public async Task UpsertResolutionResultAsync(ResolutionEntry entry, List<StreamCandidate> candidates, CancellationToken cancellationToken = default)
        {
            if (candidates == null || candidates.Count == 0) return;

            await _dbWriteGate.WaitAsync(cancellationToken);
            try
            {
                using var conn = OpenConnection();
                conn.RunInTransaction(c => InsertCandidatesCore(c, candidates));
            }
            finally
            {
                _dbWriteGate.Release();
            }
        }

        /// <summary>
        /// Returns all candidate rows for a given (imdb_id, season, episode),
        /// ordered by rank ascending (rank 0 = primary).
        /// Excludes candidates with status <c>failed</c>.
        /// Returns an empty list when no candidates have been stored yet.
        /// </summary>
        public async Task<List<StreamCandidate>> GetStreamCandidatesAsync(
            string imdbId, int? season, int? episode)
        {
            const string sql = @"
                SELECT id, aio_id, season, episode, rank,
                       provider_key, 'debrid' AS stream_type, url, headers_json,
                       quality_tier, file_name, file_size, bitrate_kbps,
                       is_cached, resolved_at, expires_at, status,
                       info_hash, file_idx, stream_key, binge_group,
                       languages, subtitles_json, probe_json, description,
                       raw_stream_json
                FROM stream_resolution_cache
                WHERE aio_id = @aio_id
                  AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
                  AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL))
                  AND status != 'failed'
                ORDER BY rank ASC;";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd,        "@aio_id", imdbId);
                BindNullableInt(cmd, "@season",  season);
                BindNullableInt(cmd, "@episode", episode);
            }, ReadStreamCandidate);
        }

        /// <summary>
        /// Gets cached ffprobe JSON for a stream candidate by its stream_key.
        /// </summary>
        public async Task<string?> GetProbeJsonAsync(string streamKey)
        {
            if (string.IsNullOrEmpty(streamKey)) return null;
            const string sql = @"
                SELECT probe_json FROM stream_resolution_cache
                WHERE stream_key = @stream_key AND probe_json IS NOT NULL
                LIMIT 1";

            return await QuerySingleAsync(sql,
                cmd => BindText(cmd, "@stream_key", streamKey),
                r => r.GetString(0)).ConfigureAwait(false);
        }

        /// <summary>
        /// Saves ffprobe JSON for all candidates sharing the same stream_key.
        /// </summary>
        public async Task SaveProbeJsonAsync(string streamKey, string probeJson)
        {
            if (string.IsNullOrEmpty(streamKey) || string.IsNullOrEmpty(probeJson)) return;
            const string sql = @"
                UPDATE stream_resolution_cache
                SET probe_json = @probe_json
                WHERE stream_key = @stream_key";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@stream_key", streamKey);
                BindText(cmd, "@probe_json", probeJson);
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Refreshes the CDN URL and expiry timestamps for a candidate identified by stream_key.
        /// Used by OpenMediaSource when a HEAD check fails and a fresh URL is obtained via infoHash.
        /// </summary>
        public async Task UpdateCandidateUrlAsync(
            string streamKey, string url, string resolvedAt, string expiresAt,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(streamKey) || string.IsNullOrEmpty(url)) return;
            const string sql = @"
                UPDATE stream_resolution_cache
                SET url = @url, url_resolved_at = @resolved_at, url_expires_at = @expires_at
                WHERE stream_key = @stream_key";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@stream_key",  streamKey);
                BindText(cmd, "@url",         url);
                BindText(cmd, "@resolved_at", resolvedAt);
                BindText(cmd, "@expires_at",  expiresAt);
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns media items (movies + series episodes) that have no <c>stream_candidates</c>
        /// rows with status != 'failed'.  Used by <c>StreamPrefetchTask</c> to populate the
        /// candidates table proactively before users browse.
        /// </summary>
        public async Task<List<UncachedItem>> GetItemsWithNoStreamCandidatesAsync(
            int limit, CancellationToken ct)
        {
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
                      SELECT 1 FROM stream_resolution_cache sc
                      WHERE lower(sc.aio_id) = lower(ids.id_value)
                        AND sc.season IS NULL
                        AND sc.status != 'failed'
                  )
                ORDER BY mi.created_at DESC
                LIMIT @limit";

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
                    ON lower(ci.imdb_id) = lower(ids.id_value)
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
                      SELECT 1 FROM stream_resolution_cache sc
                      WHERE lower(sc.aio_id) = lower(ids.id_value)
                        AND sc.season = season_num
                        AND sc.episode = episode_num
                        AND sc.status != 'failed'
                  )
                ORDER BY mi.created_at DESC
                LIMIT @limit";

            var results = new List<UncachedItem>();

            var movies = await QueryListAsync(moviesSql,
                cmd => BindInt(cmd, "@limit", limit / 2),
                r => new UncachedItem
                {
                    ImdbId    = r.GetString(0),
                    TmdbId    = r.IsDBNull(1) ? null : r.GetString(1),
                    MediaType = r.GetString(2),
                    Title     = r.IsDBNull(5) ? "" : r.GetString(5),
                }).ConfigureAwait(false);
            results.AddRange(movies);

            if (ct.IsCancellationRequested) return results;

            var remaining = Math.Max(0, limit - results.Count);
            if (remaining > 0)
            {
                var episodes = await QueryListAsync(seriesSql,
                    cmd => BindInt(cmd, "@limit", remaining),
                    r => new UncachedItem
                    {
                        ImdbId    = r.GetString(0),
                        TmdbId    = r.IsDBNull(1) ? null : r.GetString(1),
                        MediaType = r.GetString(2),
                        Season    = r.IsDBNull(3) ? (int?)null : r.GetInt(3),
                        Episode   = r.IsDBNull(4) ? (int?)null : r.GetInt(4),
                        Title     = r.IsDBNull(5) ? "" : r.GetString(5),
                    }).ConfigureAwait(false);
                results.AddRange(episodes);
            }

            return results;
        }

        /// <summary>
        /// Updates the <c>status</c> of a single candidate row identified by
        /// (imdb_id, season, episode, rank).  Used to mark a URL as
        /// <c>suspect</c> or <c>failed</c> without discarding other ranks.
        /// </summary>
        public async Task UpdateCandidateStatusAsync(
            string imdbId, int? season, int? episode, int rank, string status, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE stream_resolution_cache
                SET status = @status
                WHERE aio_id = @aio_id
                  AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
                  AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL))
                  AND rank    = @rank;";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd,        "@aio_id", imdbId);
                BindNullableInt(cmd, "@season",  season);
                BindNullableInt(cmd, "@episode", episode);
                cmd.BindParameters["@rank"].Bind(rank);
                BindText(cmd,        "@status",  status);
            }, cancellationToken);
        }

        /// <summary>
        /// Deletes all candidate rows for an item.
        /// Called by the background rescrape before writing fresh candidates.
        /// </summary>
        public async Task DeleteStreamCandidatesAsync(
            string imdbId, int? season, int? episode, CancellationToken cancellationToken = default)
        {
            const string sql = @"
                DELETE FROM stream_resolution_cache
                WHERE aio_id = @aio_id
                  AND (season  IS @season  OR (season  IS NULL AND @season  IS NULL))
                  AND (episode IS @episode OR (episode IS NULL AND @episode IS NULL));";

            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd,        "@aio_id", imdbId);
                BindNullableInt(cmd, "@season",  season);
                BindNullableInt(cmd, "@episode", episode);
            }, cancellationToken);
        }

        /// <summary>
        /// Returns per-stream-type counts of valid, unexpired candidates.
        /// Used by the dashboard Stream Health card.
        /// Key = stream_type; Value = count of valid unexpired rows.
        /// </summary>
        public Task<Dictionary<string, int>> GetCandidateCountsByTypeAsync()
        {
            const string sql = @"
                SELECT 'debrid' AS stream_type, COUNT(*) AS cnt
                FROM stream_resolution_cache
                WHERE status   = 'valid'
                  AND expires_at > datetime('now')
                GROUP BY stream_type
                ORDER BY cnt DESC;";

            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            foreach (var row in stmt.AsRows())
            {
                var key = row.IsDBNull(0) ? "unknown" : row.GetString(0);
                result[key] = row.IsDBNull(1) ? 0 : row.GetInt(1);
            }
            return Task.FromResult(result);
        }
    }
}
