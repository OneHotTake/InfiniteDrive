using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using InfiniteDrive.Services;
using InfiniteDrive.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Pre-expands series from full metadata instead of creating S01E01.strm and hoping Emby expands.
    /// Fetches complete episode list from Stremio meta endpoint, writes all .strm files at once.
    /// ONE library scan.
    /// </summary>
    public class SeriesPreExpansionService
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly StremioMetadataProvider _metadataProvider;

        public SeriesPreExpansionService(
            ILibraryManager libraryManager,
            ILogger logger,
            StremioMetadataProvider metadataProvider)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _metadataProvider = metadataProvider;

            _logger.LogWarning("[InfiniteDrive] StremioMetadataProvider not using MetadataService priority chain — using default episode fallback");
        }

        /// <summary>
        /// Expand a series catalog item to full episode .strm files using metadata.
        /// </summary>
        public async Task<bool> ExpandSeriesFromMetadataAsync(
            CatalogItem item,
            PluginConfiguration config,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation(
                    "[InfiniteDrive] SeriesPreExpansion: Starting for {ImdbId} ({Title})",
                    item.ImdbId, item.Title);

                // Fetch full metadata with all episodes
                var fullMeta = await _metadataProvider.GetFullSeriesMetaAsync(item.ImdbId, cancellationToken);
                if (fullMeta == null)
                {
                    // G2: Fallback to default episode counts when metadata unavailable
                    _logger.LogWarning(
                        "[InfiniteDrive] SeriesPreExpansion: No metadata found for {ImdbId}, " +
                        "using default {Seasons}s × {Episodes}ep fallback",
                        item.ImdbId, config.DefaultSeriesSeasons, config.DefaultSeriesEpisodesPerSeason);

                    return await WriteDefaultEpisodesAsync(item, config, cancellationToken);
                }

                // Create series folder structure — route anime to anime library, series to shows library
                var basePath = string.Equals(item.MediaType, "anime", StringComparison.OrdinalIgnoreCase)
                    ? config.SyncPathAnime
                    : config.SyncPathShows;
                var seriesPath = Path.Combine(
                    basePath,
                    StrmWriterService.SanitisePathPublic(BuildFolderName(fullMeta.GetName(), fullMeta.GetYear(), item.ImdbId)));

                Directory.CreateDirectory(seriesPath);
                _logger.LogDebug("[InfiniteDrive] SeriesPreExpansion: Series folder: {Path}", seriesPath);

                // Group episodes by season - same pattern as Gelato
                var seasonGroups = fullMeta.Videos
                    .Where(e => e.Season.HasValue && (e.Episode.HasValue || e.Number.HasValue))
                    .OrderBy(e => e.Season)
                    .ThenBy(e => e.Episode ?? e.Number)
                    .GroupBy(e => e.Season!.Value)
                    .ToList();

                if (seasonGroups.Count == 0)
                {
                    _logger.LogWarning(
                        "[InfiniteDrive] SeriesPreExpansion: No valid episodes found for {ImdbId}",
                        item.ImdbId);
                    return false;
                }

                _logger.LogInformation(
                    "[InfiniteDrive] SeriesPreExpansion: Found {SeasonCount} seasons with {TotalEpisodes} episodes for {SeriesName}",
                    seasonGroups.Count, seasonGroups.Sum(g => g.Count()), fullMeta.GetName());

                int written = 0;

                foreach (var seasonGroup in seasonGroups)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var seasonNum = seasonGroup.Key;
                    var seasonPath = Path.Combine(seriesPath, $"Season {seasonNum:D2}");
                    Directory.CreateDirectory(seasonPath);

                    foreach (var episode in seasonGroup)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var epNum = episode.Episode ?? episode.Number!.Value;

                        // Skip unaired episodes if configured
                        if (config.SkipFutureEpisodes && !episode.IsReleased(config.FutureEpisodeBufferDays))
                        {
                            _logger.LogDebug(
                                "[InfiniteDrive] SeriesPreExpansion: Skipping unaired S{Season}x{Episode}",
                                seasonNum, epNum);
                            continue;
                        }

                        // Create .strm file with signed URL
                        var strmContent = BuildStrmContent(item.ImdbId ?? item.Id, seasonNum, epNum, config);

                        var sanitisedName = StrmWriterService.SanitisePathPublic(fullMeta.GetName());
                        var fileName = $"{sanitisedName} S{seasonNum:D2}E{epNum:D2}.strm";
                        var filePath = Path.Combine(seasonPath, fileName);
                        var nfoPath = Path.ChangeExtension(filePath, ".nfo");

                        if (!File.Exists(filePath))
                        {
                            await File.WriteAllTextAsync(filePath, strmContent, Encoding.UTF8, cancellationToken);

                            // Write episode NFO file
                            await WriteEpisodeNfoFileAsync(nfoPath, episode, fullMeta, cancellationToken);

                            written++;
                            _logger.LogDebug(
                                "[InfiniteDrive] SeriesPreExpansion: Wrote {FilePath}",
                                filePath);
                        }
                    }
                }

                // Write tvshow.nfo file for Emby
                await WriteTvNfoFileAsync(seriesPath, fullMeta, item, cancellationToken);

                _logger.LogInformation(
                    "[InfiniteDrive] SeriesPreExpansion: Complete for {ImdbId} ({Title}) - {Written} new .strm files written",
                    item.ImdbId, item.Title, written);

                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[InfiniteDrive] SeriesPreExpansion: Failed for {ImdbId} ({Title})",
                    item.ImdbId, item.Title);
                return false;
            }
        }

        /// <summary>
        /// Writes default episode .strm files when series metadata is unavailable.
        /// Uses configured defaults for seasons and episodes per season.
        /// G2: Fallback for 404 responses from Stremio metadata API.
        /// </summary>
        private async Task<bool> WriteDefaultEpisodesAsync(
            CatalogItem item,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var numSeasons = config.DefaultSeriesSeasons;
            var numEpisodes = config.DefaultSeriesEpisodesPerSeason;

            _logger.LogInformation(
                "[InfiniteDrive] SeriesPreExpansion: Writing default episodes for {ImdbId} - {Seasons}s × {Episodes}ep",
                item.ImdbId, numSeasons, numEpisodes);

            // Create series folder — route anime to anime library, series to shows library
            var basePath = string.Equals(item.MediaType, "anime", StringComparison.OrdinalIgnoreCase)
                ? config.SyncPathAnime
                : config.SyncPathShows;
            var seriesPath = Path.Combine(
                basePath,
                StrmWriterService.SanitisePathPublic(BuildFolderName(item.Title, item.Year, item.ImdbId)));

            Directory.CreateDirectory(seriesPath);

            int written = 0;

            for (int season = 1; season <= numSeasons; season++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var seasonPath = Path.Combine(seriesPath, $"Season {season:D2}");
                Directory.CreateDirectory(seasonPath);

                for (int episode = 1; episode <= numEpisodes; episode++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // G3: Each episode .strm must include season+episode in signed URL
                    var strmContent = BuildStrmContent(item.ImdbId ?? item.Id, season, episode, config);

                    var sanitisedName = StrmWriterService.SanitisePathPublic(item.Title);
                    var fileName = $"{sanitisedName} S{season:D2}E{episode:D2}.strm";
                    var filePath = Path.Combine(seasonPath, fileName);

                    if (!File.Exists(filePath))
                    {
                        await File.WriteAllTextAsync(filePath, strmContent, Encoding.UTF8, cancellationToken);
                        written++;
                        _logger.LogDebug(
                            "[InfiniteDrive] SeriesPreExpansion: Wrote default {FilePath}",
                            filePath);
                    }
                }
            }

            _logger.LogInformation(
                "[InfiniteDrive] SeriesPreExpansion: Default episodes complete for {ImdbId} ({Title}) - {Written} .strm files written",
                item.ImdbId, item.Title, written);

            return true;
        }

        // ── Private helpers ──────────────────────────────────────────────────────────────────

        private static string BuildFolderName(string title, int? year, string? imdbId)
        {
            var sb = new StringBuilder(title);
            if (year.HasValue) sb.Append($" ({year})");
            if (!string.IsNullOrEmpty(imdbId)) sb.Append($" [imdbid-{imdbId}]");
            return sb.ToString();
        }

        /// <summary>
        /// Build .strm file content. Uses signed URLs when available, falls back to legacy Play endpoint.
        /// </summary>
        private static string BuildStrmContent(string id, int season, int episode, PluginConfiguration config)
        {
            // Ensure PluginSecret is initialized before accessing Configuration
            Plugin.Instance?.EnsureInitialization();

            var secret = Plugin.Instance?.Configuration?.PluginSecret;

            if (!string.IsNullOrEmpty(secret))
            {
                return PlaybackTokenService.GenerateSignedUrl(
                    config.EmbyBaseUrl, id, "series", season, episode,
                    secret,
                    TimeSpan.FromDays(config.SignatureValidityDays > 0 ? config.SignatureValidityDays : 365));
            }

            // Fallback: legacy Play endpoint with episode_id parameter
            var stremioEpisodeId = $"{id}:{season}:{episode}";
            var baseUrl = config.EmbyBaseUrl.TrimEnd('/');
            return $"{baseUrl}/InfiniteDrive/Play?episode_id={Uri.EscapeDataString(stremioEpisodeId)}";
        }

        /// <summary>
        /// Write a tvshow.nfo file so Emby can match the series by IMDB ID.
        /// </summary>
        private static async Task WriteTvNfoFileAsync(
            string seriesPath,
            StremioMeta meta,
            CatalogItem catalogItem,
            CancellationToken cancellationToken)
        {
            var nfoPath = Path.Combine(seriesPath, "tvshow.nfo");
            if (File.Exists(nfoPath))
                return; // Don't overwrite existing NFO

            var esc = System.Security.SecurityElement.Escape;
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<tvshow>");
            sb.AppendLine($"  <title>{esc(meta.GetName())}</title>");

            var year = meta.GetYear();
            if (year.HasValue)
                sb.AppendLine($"  <year>{year.Value}</year>");

            if (meta.Released.HasValue)
                sb.AppendLine($"  <premiered>{meta.Released.Value:yyyy-MM-dd}</premiered>");
            else if (meta.FirstAired.HasValue)
                sb.AppendLine($"  <premiered>{meta.FirstAired.Value:yyyy-MM-dd}</premiered>");

            // ── Provider IDs (uniqueid tags for anime plugin matching) ────────
            if (!string.IsNullOrWhiteSpace(catalogItem.ImdbId))
                sb.AppendLine($"  <uniqueid type=\"imdb\" default=\"true\">{esc(catalogItem.ImdbId)}</uniqueid>");

            var tmdb = meta.GetTmdbId() ?? catalogItem.TmdbId;
            if (!string.IsNullOrWhiteSpace(tmdb))
                sb.AppendLine($"  <uniqueid type=\"tmdb\">{esc(tmdb)}</uniqueid>");

            if (!string.IsNullOrWhiteSpace(catalogItem.TvdbId))
                sb.AppendLine($"  <uniqueid type=\"tvdb\">{esc(catalogItem.TvdbId)}</uniqueid>");

            if (!string.IsNullOrWhiteSpace(meta.KitsuId))
                sb.AppendLine($"  <uniqueid type=\"kitsu\">{esc(meta.KitsuId)}</uniqueid>");

            if (!string.IsNullOrWhiteSpace(meta.AniListId))
                sb.AppendLine($"  <uniqueid type=\"anilist\">{esc(meta.AniListId)}</uniqueid>");

            if (!string.IsNullOrWhiteSpace(meta.MalId))
                sb.AppendLine($"  <uniqueid type=\"mal\">{esc(meta.MalId)}</uniqueid>");

            // Legacy tags for Emby compatibility
            if (!string.IsNullOrWhiteSpace(catalogItem.ImdbId))
                sb.AppendLine($"  <imdbid>{esc(catalogItem.ImdbId)}</imdbid>");
            if (!string.IsNullOrWhiteSpace(tmdb))
                sb.AppendLine($"  <tmdbid>{esc(tmdb)}</tmdbid>");

            // ── Rich metadata ────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(meta.Overview))
                sb.AppendLine($"  <plot><![CDATA[{meta.Overview}]]></plot>");

            if (meta.Genres?.Count > 0)
            {
                foreach (var genre in meta.Genres)
                    sb.AppendLine($"  <genre>{esc(genre)}</genre>");
            }

            if (!string.IsNullOrWhiteSpace(meta.Status))
                sb.AppendLine($"  <status>{esc(meta.Status)}</status>");

            // ── Anime-specific fields ─────────────────────────────────────────
            var isAnime = string.Equals(catalogItem.MediaType, "anime", StringComparison.OrdinalIgnoreCase);
            if (isAnime)
                sb.AppendLine("  <displayorder>absolute</displayorder>");

            sb.AppendLine("</tvshow>");

            await File.WriteAllTextAsync(nfoPath, sb.ToString(), Encoding.UTF8, cancellationToken);
        }

        private static async Task WriteEpisodeNfoFileAsync(
            string nfoPath,
            StremioVideo episode,
            StremioMeta seriesMeta,
            CancellationToken cancellationToken = default)
        {
            if (episode.Season == null || episode.Episode == null)
                return;

            var esc = System.Security.SecurityElement.Escape;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine($"<episodedetails lockdata=\"false\">");

            // Episode title
            if (!string.IsNullOrEmpty(episode.Name))
                sb.AppendLine($"  <title>{esc(episode.Name)}</title>");

            // Episode number and season
            sb.AppendLine($"  <season>{episode.Season}</season>");
            sb.AppendLine($"  <episode>{episode.Episode}</episode>");

            // Absolute episode number for anime
            if (episode.AbsoluteEpisodeNumber.HasValue)
                sb.AppendLine($"  <displayepisodenumber>{episode.AbsoluteEpisodeNumber.Value}</displayepisodenumber>");

            // Air date
            if (episode.Released.HasValue)
                sb.AppendLine($"  <aired>{episode.Released.Value:yyyy-MM-dd}</aired>");
            else if (episode.FirstAired.HasValue)
                sb.AppendLine($"  <aired>{episode.FirstAired.Value:yyyy-MM-dd}</aired>");

            // Series title (for context)
            if (!string.IsNullOrEmpty(seriesMeta.Title))
                sb.AppendLine($"  <showtitle>{esc(seriesMeta.Title)}</showtitle>");

            sb.AppendLine("</episodedetails>");

            await File.WriteAllTextAsync(nfoPath, sb.ToString(), Encoding.UTF8, cancellationToken);
        }
    }
}
