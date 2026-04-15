using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Models;
using AioEnrichedMeta = InfiniteDrive.Services.AioMetadataClient.EnrichedMetadata;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Single authority for all NFO file generation.
    /// Two quality levels: Seed (Optimistic phase) and Enriched (Pessimistic phase).
    /// All XML encoding uses SecurityElement.Escape — no manual escaping anywhere.
    /// </summary>
    public static class NfoWriterService
    {
        private static readonly UTF8Encoding Utf8NoBom = new(false);
        private static readonly string XmlDecl = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";

        // ── Seed NFO (Optimistic phase) ──────────────────────────────────────

        /// <summary>
        /// Writes a minimal seed NFO for initial discovery.
        /// Contains only IDs and title — enough for Emby to match the item.
        /// </summary>
        public static void WriteSeedNfo(string strmPath, CatalogItem item, string? sourceType = null)
        {
            try
            {
                var nfoPath = Path.ChangeExtension(strmPath, ".nfo");
                var esc = System.Security.SecurityElement.Escape;
                var root = item.MediaType == "movie" ? "movie" : "tvshow";

                var sb = new StringBuilder();
                sb.AppendLine(XmlDecl);
                sb.AppendLine($"<{root}>");
                sb.AppendLine($"  <uniqueid type=\"imdb\">{esc(item.ImdbId ?? "")}</uniqueid>");
                if (!string.IsNullOrEmpty(item.TmdbId))
                    sb.AppendLine($"  <uniqueid type=\"tmdb\">{esc(item.TmdbId)}</uniqueid>");
                if (!string.IsNullOrEmpty(item.TvdbId))
                    sb.AppendLine($"  <uniqueid type=\"tvdb\">{esc(item.TvdbId)}</uniqueid>");
                sb.AppendLine($"  <title>{esc(item.Title)}</title>");
                if (item.Year.HasValue)
                    sb.AppendLine($"  <year>{item.Year.Value}</year>");
                if (!string.IsNullOrEmpty(sourceType))
                    sb.AppendLine($"  <source>{esc(sourceType)}</source>");
                sb.AppendLine($"</{root}>");

                File.WriteAllText(nfoPath, sb.ToString(), Utf8NoBom);
            }
            catch
            {
                // Seed NFO failure is non-fatal — Emby can still match by folder name
            }
        }

        /// <summary>
        /// Writes a minimal seed NFO for an episode.
        /// </summary>
        public static void WriteSeedEpisodeNfo(string strmPath, string seriesTitle, int season, int episode, string? episodeTitle = null)
        {
            try
            {
                var nfoPath = Path.ChangeExtension(strmPath, ".nfo");
                var esc = System.Security.SecurityElement.Escape;

                var sb = new StringBuilder();
                sb.AppendLine(XmlDecl);
                sb.AppendLine("<episodedetails>");
                sb.AppendLine($"  <title>{esc(episodeTitle ?? $"Episode {episode}")}</title>");
                sb.AppendLine($"  <season>{season}</season>");
                sb.AppendLine($"  <episode>{episode}</episode>");
                sb.AppendLine($"  <showtitle>{esc(seriesTitle)}</showtitle>");
                sb.AppendLine("</episodedetails>");

                File.WriteAllText(nfoPath, sb.ToString(), Utf8NoBom);
            }
            catch
            {
                // Non-fatal
            }
        }

        // ── Identity Hint NFO (RefreshTask) ──────────────────────────────────

        /// <summary>
        /// Writes an identity hint NFO with just a uniqueid tag.
        /// Used by RefreshTask to give Emby enough to scrape.
        /// </summary>
        public static void WriteIdentityHintNfo(string nfoPath, string uniqueidType, string uniqueidValue, string rootElement)
        {
            var tmpPath = nfoPath + ".tmp";
            try
            {
                var esc = System.Security.SecurityElement.Escape;
                var sb = new StringBuilder();
                sb.AppendLine(XmlDecl);
                sb.AppendLine($"<{rootElement} lockdata=\"false\">");
                sb.AppendLine($"  <uniqueid type=\"{esc(uniqueidType)}\" default=\"true\">");
                sb.AppendLine($"    {esc(uniqueidValue)}");
                sb.AppendLine("  </uniqueid>");
                sb.AppendLine($"</{rootElement}>");

                File.WriteAllText(tmpPath, sb.ToString(), Utf8NoBom);
                File.Move(tmpPath, nfoPath, overwrite: true);
            }
            catch
            {
                if (File.Exists(tmpPath)) File.Delete(tmpPath);
            }
        }

        // ── Enriched NFO (Pessimistic phase) ─────────────────────────────────

        /// <summary>
        /// Writes a full enriched NFO with all available metadata.
        /// Used by MarvinTask, RefreshTask, MetadataFallbackTask during Pessimistic phase.
        /// </summary>
        public static void WriteEnrichedNfo(string nfoPath, CatalogItem item, AioMeta meta)
        {
            var tmpPath = nfoPath + ".tmp";
            try
            {
                var esc = System.Security.SecurityElement.Escape;
                var isMovie = item.MediaType == "movie";
                var root = isMovie ? "movie" : "tvshow";

                var sb = new StringBuilder();
                sb.AppendLine(XmlDecl);
                sb.AppendLine($"<{root}>");

                var title = meta.Name ?? item.Title;
                sb.AppendLine($"  <title>{esc(title)}</title>");

                var year = meta.Year ?? item.Year;
                if (year.HasValue)
                    sb.AppendLine($"  <year>{year.Value}</year>");

                // Original title and sort title for anime/series
                if (!isMovie || item.MediaType == "anime")
                {
                    var originalTitle = meta.OriginalTitle ?? title;
                    if (!string.IsNullOrEmpty(originalTitle))
                        sb.AppendLine($"  <originaltitle>{esc(originalTitle)}</originaltitle>");

                    var sortTitle = BuildSortTitle(title);
                    if (!string.IsNullOrEmpty(sortTitle))
                        sb.AppendLine($"  <sorttitle>{esc(sortTitle)}</sorttitle>");
                }

                if (!string.IsNullOrEmpty(meta.Description))
                    sb.AppendLine($"  <plot>{esc(meta.Description)}</plot>");

                // IDs
                if (!string.IsNullOrEmpty(meta.ImdbId ?? item.ImdbId))
                    sb.AppendLine($"  <uniqueid type=\"imdb\" default=\"true\">{esc(meta.ImdbId ?? item.ImdbId!)}</uniqueid>");
                if (!string.IsNullOrEmpty(meta.TmdbId ?? item.TmdbId))
                    sb.AppendLine($"  <uniqueid type=\"tmdb\">{esc(meta.TmdbId ?? item.TmdbId!)}</uniqueid>");

                if (!string.IsNullOrEmpty(meta.ImdbRating))
                    sb.AppendLine($"  <rating>{esc(meta.ImdbRating)}</rating>");
                if (!string.IsNullOrEmpty(meta.Runtime))
                    sb.AppendLine($"  <runtime>{esc(meta.Runtime)}</runtime>");

                if (meta.Genres?.Count > 0)
                    foreach (var genre in meta.Genres)
                        sb.AppendLine($"  <genre>{esc(genre)}</genre>");

                if (!string.IsNullOrEmpty(meta.Poster))
                    sb.AppendLine($"  <thumb aspect=\"poster\">{esc(meta.Poster)}</thumb>");
                if (!string.IsNullOrEmpty(meta.Background))
                    sb.AppendLine($"  <fanart><thumb>{esc(meta.Background)}</thumb></fanart>");
                if (!string.IsNullOrEmpty(meta.Director))
                    sb.AppendLine($"  <director>{esc(meta.Director)}</director>");
                if (meta.Cast?.Count > 0)
                    foreach (var actor in meta.Cast)
                        sb.AppendLine($"  <actor><name>{esc(actor)}</name></actor>");

                sb.AppendLine($"</{root}>");

                Directory.CreateDirectory(Path.GetDirectoryName(nfoPath)!);
                File.WriteAllText(tmpPath, sb.ToString(), Utf8NoBom);
                File.Move(tmpPath, nfoPath, overwrite: true);
            }
            catch
            {
                if (File.Exists(tmpPath)) File.Delete(tmpPath);
            }
        }

        /// <summary>
        /// Writes enriched NFO content to multiple version slot files.
        /// Returns the count of slots written.
        /// </summary>
        public static async Task<int> WriteEnrichedNfoForSlots(string folderPath, string baseName, CatalogItem item, AioMeta meta)
        {
            var slotRepo = Plugin.Instance?.VersionSlotRepository;
            if (slotRepo == null) return 0;
            var slots = await slotRepo.GetEnabledSlotsAsync(default);
            if (slots == null || slots.Count == 0) return 0;

            var esc = System.Security.SecurityElement.Escape;
            var isMovie = item.MediaType == "movie";
            var root = isMovie ? "movie" : "tvshow";
            var title = meta.Name ?? item.Title;
            var year = meta.Year ?? item.Year;

            // Build shared NFO content
            var sb = new StringBuilder();
            sb.AppendLine(XmlDecl);
            sb.AppendLine($"<{root}>");
            sb.AppendLine($"  <title>{esc(title)}</title>");
            if (year.HasValue)
                sb.AppendLine($"  <year>{year.Value}</year>");
            if (!string.IsNullOrEmpty(meta.Description))
                sb.AppendLine($"  <plot>{esc(meta.Description)}</plot>");
            if (!string.IsNullOrEmpty(meta.ImdbId ?? item.ImdbId))
                sb.AppendLine($"  <uniqueid type=\"imdb\" default=\"true\">{esc(meta.ImdbId ?? item.ImdbId!)}</uniqueid>");
            else if (!string.IsNullOrEmpty(meta.TmdbId ?? item.TmdbId))
                sb.AppendLine($"  <uniqueid type=\"tmdb\" default=\"true\">{esc(meta.TmdbId ?? item.TmdbId!)}</uniqueid>");
            if (meta.Genres?.Count > 0)
                foreach (var genre in meta.Genres)
                    sb.AppendLine($"  <genre>{esc(genre)}</genre>");
            sb.AppendLine($"</{root}>");

            var content = sb.ToString();
            var defaultSlot = slots.FirstOrDefault(s => s.IsDefault) ?? slots[0];
            var written = 0;

            foreach (var slot in slots)
            {
                var fileName = $"{baseName}{(slot.IsDefault ? "" : $"_{slot.SlotKey}")}.nfo";
                var fullPath = Path.Combine(folderPath, fileName);
                var tmpPath = fullPath + ".tmp";

                try
                {
                    File.WriteAllText(tmpPath, content, Utf8NoBom);
                    File.Move(tmpPath, fullPath, overwrite: true);
                    written++;
                }
                catch
                {
                    if (File.Exists(tmpPath)) File.Delete(tmpPath);
                }
            }

            return written;
        }

        /// <summary>
        /// Writes enriched NFO content to multiple version slot files using EnrichedMetadata.
        /// Used by MarvinTask and RefreshTask enrichment.
        /// </summary>
        public static async Task<int> WriteEnrichedNfoForSlots(string folderPath, string baseName, CatalogItem item, AioEnrichedMeta meta)
        {
            var slotRepo = Plugin.Instance?.VersionSlotRepository;
            if (slotRepo == null) return 0;
            var slots = await slotRepo.GetEnabledSlotsAsync(default);
            if (slots == null || slots.Count == 0) return 0;

            var esc = System.Security.SecurityElement.Escape;
            var isMovie = item.MediaType == "movie";
            var root = isMovie ? "movie" : "tvshow";
            var title = meta.Name ?? item.Title;
            var year = meta.Year ?? item.Year;

            var sb = new StringBuilder();
            sb.AppendLine(XmlDecl);
            sb.AppendLine($"<{root}>");
            sb.AppendLine($"  <title>{esc(title)}</title>");
            if (year.HasValue)
                sb.AppendLine($"  <year>{year.Value}</year>");
            if (!string.IsNullOrEmpty(meta.Description))
                sb.AppendLine($"  <plot>{esc(meta.Description)}</plot>");
            if (!string.IsNullOrEmpty(meta.ImdbId))
                sb.AppendLine($"  <uniqueid type=\"imdb\" default=\"true\">{esc(meta.ImdbId)}</uniqueid>");
            else if (!string.IsNullOrEmpty(meta.TmdbId))
                sb.AppendLine($"  <uniqueid type=\"tmdb\" default=\"true\">{esc(meta.TmdbId)}</uniqueid>");
            if (meta.Genres?.Count > 0)
                foreach (var genre in meta.Genres)
                    sb.AppendLine($"  <genre>{esc(genre)}</genre>");
            sb.AppendLine($"</{root}>");

            var content = sb.ToString();
            var written = 0;

            foreach (var slot in slots)
            {
                var fileName = $"{baseName}{(slot.IsDefault ? "" : $"_{slot.SlotKey}")}.nfo";
                var fullPath = Path.Combine(folderPath, fileName);
                var tmpPath = fullPath + ".tmp";

                try
                {
                    File.WriteAllText(tmpPath, content, Utf8NoBom);
                    File.Move(tmpPath, fullPath, overwrite: true);
                    written++;
                }
                catch
                {
                    if (File.Exists(tmpPath)) File.Delete(tmpPath);
                }
            }

            return written;
        }

        // ── Episode NFO (SeriesPreExpansionService) ──────────────────────────

        /// <summary>
        /// Writes an episode NFO with full metadata from Stremio.
        /// </summary>
        public static async Task WriteEpisodeNfoAsync(
            string nfoPath,
            StremioVideo episode,
            StremioMeta seriesMeta,
            CancellationToken ct = default)
        {
            if (episode.Season == null || episode.Episode == null)
                return;

            var esc = System.Security.SecurityElement.Escape;
            var sb = new StringBuilder();
            sb.AppendLine(XmlDecl);
            sb.AppendLine("<episodedetails lockdata=\"false\">");

            if (!string.IsNullOrEmpty(episode.Name))
                sb.AppendLine($"  <title>{esc(episode.Name)}</title>");

            sb.AppendLine($"  <season>{episode.Season}</season>");
            sb.AppendLine($"  <episode>{episode.Episode}</episode>");

            if (episode.AbsoluteEpisodeNumber.HasValue)
                sb.AppendLine($"  <displayepisodenumber>{episode.AbsoluteEpisodeNumber.Value}</displayepisodenumber>");

            if (episode.Released.HasValue)
                sb.AppendLine($"  <aired>{episode.Released.Value:yyyy-MM-dd}</aired>");
            else if (episode.FirstAired.HasValue)
                sb.AppendLine($"  <aired>{episode.FirstAired.Value:yyyy-MM-dd}</aired>");

            if (!string.IsNullOrEmpty(seriesMeta.Title))
                sb.AppendLine($"  <showtitle>{esc(seriesMeta.Title)}</showtitle>");

            sb.AppendLine("</episodedetails>");

            await File.WriteAllTextAsync(nfoPath, sb.ToString(), Encoding.UTF8, ct);
        }

        /// <summary>
        /// Writes a basic episode NFO with just title, season, episode, and showtitle.
        /// </summary>
        public static async Task WriteEpisodeNfoAsync(
            string nfoPath,
            string seriesTitle,
            int season,
            int episode,
            CancellationToken ct = default)
        {
            var esc = System.Security.SecurityElement.Escape;
            var sb = new StringBuilder();
            sb.AppendLine(XmlDecl);
            sb.AppendLine("<episodedetails lockdata=\"false\">");
            sb.AppendLine($"  <title>Episode {episode}</title>");
            sb.AppendLine($"  <season>{season}</season>");
            sb.AppendLine($"  <episode>{episode}</episode>");
            sb.AppendLine($"  <showtitle>{esc(seriesTitle)}</showtitle>");
            sb.AppendLine("</episodedetails>");

            await File.WriteAllTextAsync(nfoPath, sb.ToString(), Encoding.UTF8, ct);
        }

        // ── TV Show NFO (SeriesPreExpansionService) ──────────────────────────

        /// <summary>
        /// Writes a tvshow.nfo file so Emby can match the series by IMDB ID.
        /// Skips if tvshow.nfo already exists.
        /// </summary>
        public static async Task WriteTvShowNfoAsync(
            string seriesPath,
            StremioMeta meta,
            CatalogItem catalogItem,
            CancellationToken ct = default)
        {
            var nfoPath = Path.Combine(seriesPath, "tvshow.nfo");
            if (File.Exists(nfoPath))
                return;

            var esc = System.Security.SecurityElement.Escape;
            var sb = new StringBuilder();
            sb.AppendLine(XmlDecl);
            sb.AppendLine("<tvshow>");
            sb.AppendLine($"  <title>{esc(meta.GetName())}</title>");

            var year = meta.GetYear();
            if (year.HasValue)
                sb.AppendLine($"  <year>{year.Value}</year>");

            if (meta.Released.HasValue)
                sb.AppendLine($"  <premiered>{meta.Released.Value:yyyy-MM-dd}</premiered>");
            else if (meta.FirstAired.HasValue)
                sb.AppendLine($"  <premiered>{meta.FirstAired.Value:yyyy-MM-dd}</premiered>");

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

            if (!string.IsNullOrWhiteSpace(catalogItem.ImdbId))
                sb.AppendLine($"  <imdbid>{esc(catalogItem.ImdbId)}</imdbid>");
            if (!string.IsNullOrWhiteSpace(tmdb))
                sb.AppendLine($"  <tmdbid>{esc(tmdb)}</tmdbid>");

            if (!string.IsNullOrWhiteSpace(meta.Overview))
                sb.AppendLine($"  <plot><![CDATA[{meta.Overview}]]></plot>");

            if (meta.Genres?.Count > 0)
                foreach (var genre in meta.Genres)
                    sb.AppendLine($"  <genre>{esc(genre)}</genre>");

            if (!string.IsNullOrWhiteSpace(meta.Status))
                sb.AppendLine($"  <status>{esc(meta.Status)}</status>");

            var isAnime = string.Equals(catalogItem.MediaType, "anime", StringComparison.OrdinalIgnoreCase);
            if (isAnime)
                sb.AppendLine("  <displayorder>absolute</displayorder>");

            sb.AppendLine("</tvshow>");

            await File.WriteAllTextAsync(nfoPath, sb.ToString(), Encoding.UTF8, ct);
        }

        // ── Version slot copy ─────────────────────────────────────────────────

        /// <summary>
        /// Copies the base NFO file to a version slot NFO path.
        /// </summary>
        public static void CopyNfoForVersion(string baseStrmPath, string versionStrmPath)
        {
            var baseNfo = Path.ChangeExtension(baseStrmPath, ".nfo");
            var versionNfo = Path.ChangeExtension(versionStrmPath, ".nfo");

            if (File.Exists(baseNfo))
            {
                try { File.Copy(baseNfo, versionNfo, overwrite: true); }
                catch { /* non-fatal */ }
            }
        }

        // ── Helper ────────────────────────────────────────────────────────────

        private static string BuildSortTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return title;
            var lower = title.ToLowerInvariant();
            if (lower.StartsWith("the ")) return title[4..];
            if (lower.StartsWith("a ")) return title[2..];
            if (lower.StartsWith("an ")) return title[3..];
            return title;
        }
    }
}
