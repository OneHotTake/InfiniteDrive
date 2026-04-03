using System;
using System.Text.Json;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Detects anime content using a two-tier approach.
    /// Sprint 100B-02: Anime path routing (two-tier).
    /// </summary>
    public static class AnimeDetector
    {
        /// <summary>
        /// Detects if an item is anime using two-tier detection.
        /// Tier 1: catalogType == "anime"
        /// Tier 2: has AniList/Kitsu/MAL without IMDB
        /// </summary>
        /// <param name="catalogType">The raw catalog type from the source.</param>
        /// <param name="meta">Optional metadata element for Tier 2 detection.</param>
        /// <param name="imdbId">The IMDB ID (empty if not available).</param>
        /// <returns>True if the item is anime.</returns>
        public static bool IsAnime(string? catalogType, JsonElement? meta, string imdbId)
        {
            // Tier 1: Explicit catalog type
            if (!string.IsNullOrEmpty(catalogType) &&
                string.Equals(catalogType, "anime", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Tier 2: Has anime provider IDs but no IMDB ID
            if (string.IsNullOrEmpty(imdbId) && meta != null)
            {
                var hasAnilist = HasMetaId(meta, "anilist_id");
                var hasKitsu = HasMetaId(meta, "kitsu_id");
                var hasMal = HasMetaId(meta, "mal_id");

                if (hasAnilist || hasKitsu || hasMal)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if metadata contains a non-empty anime provider ID.
        /// </summary>
        private static bool HasMetaId(JsonElement meta, string idField)
        {
            if (meta.TryGetProperty(idField, out var idProp))
            {
                var id = idProp.GetString();
                return !string.IsNullOrEmpty(id);
            }
            return false;
        }
    }
}
