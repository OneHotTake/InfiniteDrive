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
        // ── Discover Catalog ────────────────────────────────────────────────────

        public async Task UpsertDiscoverCatalogEntryAsync(DiscoverCatalogEntry entry, CancellationToken cancellationToken = default)
        {
            const string sql = @"
INSERT OR REPLACE INTO discover_catalog
    (id, imdb_id, title, year, media_type, poster_url, backdrop_url, overview,
     genres, imdb_rating, certification, catalog_source, is_in_user_library, added_at, updated_at)
VALUES
    (@id, @imdb_id, @title, @year, @media_type, @poster_url, @backdrop_url, @overview,
     @genres, @imdb_rating, @certification, @catalog_source, @in_library, @added_at, datetime('now'))";
            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@id", entry.Id);
                BindText(cmd, "@imdb_id", entry.ImdbId);
                BindText(cmd, "@title", entry.Title);
                BindNullableInt(cmd, "@year", entry.Year);
                BindText(cmd, "@media_type", entry.MediaType);
                BindNullableText(cmd, "@poster_url", entry.PosterUrl);
                BindNullableText(cmd, "@backdrop_url", entry.BackdropUrl);
                BindNullableText(cmd, "@overview", entry.Overview);
                BindNullableText(cmd, "@genres", entry.Genres);
                if (entry.ImdbRating.HasValue)
                    cmd.BindParameters["@imdb_rating"].Bind(entry.ImdbRating.Value);
                else
                    cmd.BindParameters["@imdb_rating"].BindNull();
                BindNullableText(cmd, "@certification", entry.Certification);
                BindText(cmd, "@catalog_source", entry.CatalogSource);
                cmd.BindParameters["@in_library"].Bind(entry.IsInUserLibrary ? 1 : 0);
                BindText(cmd, "@added_at", entry.AddedAt);
            }, cancellationToken);
        }

        public async Task<List<DiscoverCatalogEntry>> GetDiscoverCatalogAsync(int limit = 100, int offset = 0)
        {
            return await GetDiscoverCatalogAsync(limit, offset, null, null);
        }

        public async Task<List<DiscoverCatalogEntry>> GetDiscoverCatalogAsync(int limit, int offset, string? mediaType = null, string? sortBy = null)
        {
            var sql = @"
SELECT id, imdb_id, title, year, media_type, poster_url, backdrop_url, overview,
       genres, imdb_rating, certification, catalog_source, is_in_user_library, added_at
FROM discover_catalog
WHERE NOT EXISTS (
    SELECT 1 FROM catalog_items ci WHERE ci.imdb_id = discover_catalog.imdb_id AND ci.blocked_at IS NOT NULL
)";

            if (mediaType != null)
            {
                sql += " AND media_type = @media_type";
            }

            // Handle sorting
            sql += sortBy?.ToLowerInvariant() switch
            {
                "imdb_rating" => " ORDER BY imdb_rating DESC NULLS LAST, title ASC",
                "title" => " ORDER BY title ASC",
                "added_at" or _ => " ORDER BY added_at DESC"
            };

            sql += " LIMIT @limit OFFSET @offset";

            return await QueryListAsync(sql, cmd =>
            {
                cmd.BindParameters["@limit"].Bind(limit);
                cmd.BindParameters["@offset"].Bind(offset);
                if (mediaType != null) BindText(cmd, "@media_type", mediaType);
            }, ReadDiscoverCatalogEntry);
        }

        public async Task<List<DiscoverCatalogEntry>> GetDiscoverCatalogBySourceAsync(
            string catalogSource, string? genre = null, int limit = 42, int offset = 0)
        {
            var sql = @"SELECT id, imdb_id, title, year, media_type, poster_url, backdrop_url, overview,
       genres, imdb_rating, certification, catalog_source, is_in_user_library, added_at
FROM discover_catalog
WHERE catalog_source = @source
  AND is_in_user_library = 0";
            if (genre != null) sql += " AND genres LIKE '%' || @genre || '%'";
            sql += " ORDER BY added_at DESC LIMIT @limit OFFSET @offset";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@source", catalogSource);
                if (genre != null) BindText(cmd, "@genre", genre);
                cmd.BindParameters["@limit"].Bind(limit);
                cmd.BindParameters["@offset"].Bind(offset);
            }, ReadDiscoverCatalogEntry);
        }

        public async Task<List<string>> GetDiscoverMediaTypesAsync()
        {
            const string sql = "SELECT DISTINCT media_type FROM discover_catalog WHERE is_in_user_library = 0 ORDER BY media_type";
            return await QueryListAsync(sql, _ => { }, row => row.GetString(0));
        }

        public async Task<List<string>> GetDiscoverCatalogSourcesAsync(string mediaType)
        {
            const string sql = "SELECT DISTINCT catalog_source FROM discover_catalog WHERE media_type = @media_type AND is_in_user_library = 0 ORDER BY catalog_source";
            return await QueryListAsync(sql, cmd => BindText(cmd, "@media_type", mediaType), row => row.GetString(0));
        }

        public Task<int> GetDiscoverCatalogCountAsync()
        {
            return GetDiscoverCatalogCountAsync(null);
        }

        public Task<int> GetDiscoverCatalogCountAsync(string? mediaType)
        {
            var sql = @"SELECT COUNT(*) FROM discover_catalog
WHERE NOT EXISTS (
    SELECT 1 FROM catalog_items ci WHERE ci.imdb_id = discover_catalog.imdb_id AND ci.blocked_at IS NOT NULL
)";
            if (mediaType != null)
            {
                sql += " AND media_type = @media_type";
            }

            using var conn = OpenConnection();
            using var stmt = conn.PrepareStatement(sql);
            if (mediaType != null)
            {
                stmt.BindParameters["@media_type"].Bind(mediaType);
            }

            foreach (var row in stmt.AsRows())
                return Task.FromResult(row.IsDBNull(0) ? 0 : row.GetInt(0));
            return Task.FromResult(0);
        }

        public async Task<List<DiscoverCatalogEntry>> SearchDiscoverCatalogAsync(string query, string? mediaType = null)
        {
            // Escape FTS5 special characters in the query
            // Double quotes around the query make it an exact phrase match
            var ftsQuery = "\"" + query.Replace("\"", "\"\"") + "\"";

            var sql = @"
SELECT dc.id, dc.imdb_id, dc.title, dc.year, dc.media_type, dc.poster_url, dc.backdrop_url, dc.overview,
       dc.genres, dc.imdb_rating, dc.certification, dc.catalog_source, dc.is_in_user_library, dc.added_at
FROM discover_catalog dc
JOIN discover_catalog_fts fts ON dc.rowid = fts.rowid
WHERE discover_catalog_fts MATCH @query
  AND NOT EXISTS (
    SELECT 1 FROM catalog_items ci WHERE ci.imdb_id = dc.imdb_id AND ci.blocked_at IS NOT NULL
  )";
            if (mediaType != null)
                sql += " AND dc.media_type = @media_type";
            sql += " ORDER BY dc.is_in_user_library DESC, dc.title ASC LIMIT 50";

            return await QueryListAsync(sql, cmd =>
            {
                BindText(cmd, "@query", ftsQuery);
                if (mediaType != null) BindText(cmd, "@media_type", mediaType);
            }, ReadDiscoverCatalogEntry);
        }

        public async Task<DiscoverCatalogEntry?> GetDiscoverCatalogEntryByAioIdAsync(string imdbId)
        {
            const string sql = @"
SELECT id, imdb_id, title, year, media_type, poster_url, backdrop_url, overview,
       genres, imdb_rating, certification, catalog_source, is_in_user_library, added_at
FROM discover_catalog
WHERE imdb_id = @imdb_id
  AND NOT EXISTS (
    SELECT 1 FROM catalog_items ci WHERE ci.imdb_id = @imdb_id AND ci.blocked_at IS NOT NULL
  )
LIMIT 1";
            return await QuerySingleAsync(sql, cmd =>
            {
                BindText(cmd, "@imdb_id", imdbId);
            }, ReadDiscoverCatalogEntry);
        }

        public async Task UpdateDiscoverCatalogLibraryStatusAsync(string imdbId, bool isInLibrary, CancellationToken cancellationToken = default)
        {
            const string sql = @"
UPDATE discover_catalog
SET is_in_user_library = @in_library, updated_at = datetime('now')
WHERE imdb_id = @imdb_id";
            await ExecuteWriteAsync(sql, cmd =>
            {
                cmd.BindParameters["@in_library"].Bind(isInLibrary ? 1 : 0);
                BindText(cmd, "@imdb_id", imdbId);
            }, cancellationToken);
        }

        public async Task ClearDiscoverCatalogBySourceAsync(string catalogSource, string? mediaType = null, CancellationToken cancellationToken = default)
        {
            var sql = mediaType != null
                ? "DELETE FROM discover_catalog WHERE catalog_source = @source AND media_type = @media_type;"
                : "DELETE FROM discover_catalog WHERE catalog_source = @source;";
            await ExecuteWriteAsync(sql, cmd =>
            {
                BindText(cmd, "@source", catalogSource);
                if (mediaType != null) BindText(cmd, "@media_type", mediaType);
            }, cancellationToken);
        }

        /// <summary>
        /// Updates the certification (MPAA/TV rating) for a specific discover catalog item.
        /// Sprint 209: Used when fetching certifications from TMDB.
        /// </summary>
        public async Task UpdateDiscoverCertificationAsync(string imdbId, string? certification, CancellationToken cancellationToken = default)
        {
            const string sql = @"
UPDATE discover_catalog
SET certification = @certification, updated_at = datetime('now')
WHERE imdb_id = @imdb_id";
            await ExecuteWriteAsync(sql, cmd =>
            {
                BindNullableText(cmd, "@certification", certification);
                BindText(cmd, "@imdb_id", imdbId);
            }, cancellationToken);
        }

        /// <summary>
        /// Gets discover catalog items that need certification fetched (certification IS NULL).
        /// Returns list of (imdb_id, tmdb_id) tuples.
        /// Sprint 209: Used to batch-fetch certifications from TMDB.
        /// </summary>
        public async Task<List<(string ImdbId, string? TmdbId)>> GetDiscoverCatalogNeedingCertificationAsync(int limit, CancellationToken cancellationToken = default)
        {
            const string sql = @"
SELECT dc.imdb_id, ci.tmdb_id
FROM discover_catalog dc
LEFT JOIN catalog_items ci ON dc.imdb_id = ci.imdb_id
WHERE dc.certification IS NULL
LIMIT @limit";

            var results = new List<(string, string?)>();
            using var conn = OpenConnection();
            using var cmd = conn.PrepareStatement(sql);
            cmd.BindParameters["@limit"].Bind(limit);

            foreach (var row in cmd.AsRows())
            {
                var imdbId = row.GetString(0);
                var tmdbId = row.IsDBNull(1) ? null : row.GetString(1);
                results.Add((imdbId, tmdbId));
            }

            return await Task.FromResult(results);
        }

        // ── Channel-specific methods ─────────────────────────────────────────────

        /// <summary>
        /// Get movies from discover catalog for channel browsing.
        /// </summary>
        public List<object> GetDiscoverCatalogMovies(int startIndex = 0, int limit = 100)
        {
            var items = new List<object>();
            using var conn = OpenConnection();
            var sql = @"
SELECT id, imdb_id, title, year, media_type, poster_url, backdrop_url, overview,
       genres, imdb_rating, certification, catalog_source, is_in_user_library, added_at
FROM discover_catalog
WHERE media_type = 'movie'
ORDER BY imdb_rating DESC, title ASC
LIMIT @limit OFFSET @offset";

            using var cmd = conn.PrepareStatement(sql);
            cmd.BindParameters["@limit"].Bind(limit);
            cmd.BindParameters["@offset"].Bind(startIndex);

            foreach (var row in cmd.AsRows())
            {
                var entry = ReadDiscoverCatalogEntry(row);
                var channelItem = MapToChannelItem(entry);
                items.Add(channelItem);
            }

            return items;
        }

        /// <summary>
        /// Get TV shows from discover catalog for channel browsing.
        /// </summary>
        public List<object> GetDiscoverCatalogTvShows(int startIndex = 0, int limit = 100)
        {
            var items = new List<object>();
            using var conn = OpenConnection();
            var sql = @"
SELECT id, imdb_id, title, year, media_type, poster_url, backdrop_url, overview,
       genres, imdb_rating, certification, catalog_source, is_in_user_library, added_at
FROM discover_catalog
WHERE media_type = 'series'
ORDER BY imdb_rating DESC, title ASC
LIMIT @limit OFFSET @offset";

            using var cmd = conn.PrepareStatement(sql);
            cmd.BindParameters["@limit"].Bind(limit);
            cmd.BindParameters["@offset"].Bind(startIndex);

            foreach (var row in cmd.AsRows())
            {
                var entry = ReadDiscoverCatalogEntry(row);
                var channelItem = MapToChannelItem(entry);
                items.Add(channelItem);
            }

            return items;
        }

        /// <summary>
        /// Get total movie count in discover catalog.
        /// </summary>
        public int GetDiscoverCatalogMovieCount()
        {
            const string sql = "SELECT COUNT(*) FROM discover_catalog WHERE media_type = 'movie'";
            using var conn = OpenConnection();
            using var cmd = conn.PrepareStatement(sql);
            foreach (var row in cmd.AsRows())
            {
                return row.GetInt(0);
            }
            return 0;
        }

        /// <summary>
        /// Get total TV show count in discover catalog.
        /// </summary>
        public int GetDiscoverCatalogTvShowCount()
        {
            const string sql = "SELECT COUNT(*) FROM discover_catalog WHERE media_type = 'series'";
            using var conn = OpenConnection();
            using var cmd = conn.PrepareStatement(sql);
            foreach (var row in cmd.AsRows())
            {
                return row.GetInt(0);
            }
            return 0;
        }

        /// <summary>
        /// Get a specific discover catalog item by ID.
        /// </summary>
        public object? GetDiscoverCatalogItem(string id)
        {
            const string sql = @"
SELECT id, imdb_id, title, year, media_type, poster_url, backdrop_url, overview,
       genres, imdb_rating, certification, catalog_source, is_in_user_library, added_at
FROM discover_catalog
WHERE id = @id
LIMIT 1";

            using var conn = OpenConnection();
            using var cmd = conn.PrepareStatement(sql);
            BindText(cmd, "@id", id);

            foreach (var row in cmd.AsRows())
            {
                var entry = ReadDiscoverCatalogEntry(row);
                return MapToChannelItem(entry);
            }

            return null;
        }

        /// <summary>
        /// Convert DiscoverCatalogEntry to a simple object for channel display.
        /// </summary>
        private object MapToChannelItem(DiscoverCatalogEntry entry)
        {
            // Return a simple anonymous object that Emby can understand
            // ChannelItemInfo might not be available in the Data project
            _logger.LogInformation("[Database] Mapping item: {Title} ({ImdbId})", entry.Title, entry.ImdbId);

            // For media items, use null or omit the Type property
            // Only folders should specify ChannelItemType.Folder
            return new
            {
                Name = entry.Title,
                Id = $"discover:{entry.ImdbId}",
                // Type = ChannelItemType.Media,  // Don't set type for media items
                ImageUrl = entry.PosterUrl,
                Overview = entry.Overview,
                CommunityRating = entry.ImdbRating ?? 0,
                ProductionYear = entry.Year ?? 0,
                PremiereDate = entry.Year.HasValue ? new DateTime(entry.Year.Value, 1, 1) : (DateTime?)null,
                GenreList = entry.Genres?.Split(',').Where(g => !string.IsNullOrWhiteSpace(g)).ToList() ?? new List<string>(),
                Tags = new List<string> { "InfiniteDrive" },
                ProviderIds = new Dictionary<string, string>
                {
                    ["imdb"] = entry.ImdbId
                }
            };
        }
    }
}
