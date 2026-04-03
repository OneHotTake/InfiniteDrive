using System;
using System.Text.Json;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Detects anime content using a three-tier approach.
    /// Sprint 100B-02: Anime path routing (two-tier).
    /// Sprint 101A-03: Anime subtype routing (Tier 3 for OVA/ONA/SPECIAL).
    /// </summary>
    public static class AnimeDetector
    {
        /// <summary>
        /// Anime subtype enumeration.
        /// Sprint 101A-03: Anime subtype routing.
        /// </summary>
        public enum AnimeSubtype
        {
            /// <summary>Regular TV series anime.</summary>
            TvSeries = 0,

            /// <summary>Original Video Animation (direct-to-video).</summary>
            OVA = 1,

            /// <summary>Original Net Animation (web series).</summary>
            ONA = 2,

            /// <summary>Special episodes (movies, specials, shorts).</summary>
            Special = 3,

            /// <summary>Unknown/undetermined subtype.</summary>
            Unknown = 99
        }

        /// <summary>
        /// Detects if an item is anime using three-tier detection.
        /// Tier 1: catalogType == "anime"
        /// Tier 2: has AniList/Kitsu/MAL without IMDB
        /// Tier 3: subtype-based (OVA/ONA/SPECIAL in metadata)
        /// </summary>
        /// <param name="catalogType">The raw catalog type from the source.</param>
        /// <param name="meta">Optional metadata element for Tier 2 and Tier 3 detection.</param>
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
                var hasAnilist = HasMetaId(meta.Value, "anilist_id");
                var hasKitsu = HasMetaId(meta.Value, "kitsu_id");
                var hasMal = HasMetaId(meta.Value, "mal_id");

                if (hasAnilist || hasKitsu || hasMal)
                {
                    return true;
                }
            }

            // Tier 3: Subtype-based detection (OVA/ONA/SPECIAL)
            // Some providers indicate anime through subtype fields
            if (meta != null)
            {
                var subtype = GetAnimeSubtype(meta.Value);
                if (subtype != AnimeSubtype.TvSeries && subtype != AnimeSubtype.Unknown)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Detects anime subtype from metadata.
        /// Sprint 101A-03: Anime subtype routing.
        /// Checks for OVA, ONA, and SPECIAL indicators in metadata fields.
        /// </summary>
        /// <param name="meta">The metadata element to examine.</param>
        /// <returns>The detected anime subtype.</returns>
        public static AnimeSubtype GetAnimeSubtype(JsonElement meta)
        {
            // Check for explicit subtype field
            if (meta.TryGetProperty("subtype", out var subtypeProp))
            {
                var subtype = subtypeProp.GetString()?.ToLowerInvariant();
                if (!string.IsNullOrEmpty(subtype))
                {
                    return subtype switch
                    {
                        "ova" => AnimeSubtype.OVA,
                        "ona" => AnimeSubtype.ONA,
                        "special" or "specials" => AnimeSubtype.Special,
                        "tv" or "series" => AnimeSubtype.TvSeries,
                        _ => AnimeSubtype.Unknown
                    };
                }
            }

            // Check title for common OVA/ONA patterns
            if (meta.TryGetProperty("name", out var nameProp))
            {
                var name = nameProp.GetString()?.ToUpperInvariant() ?? string.Empty;
                if (name.Contains("OVA"))
                    return AnimeSubtype.OVA;
                if (name.Contains("ONA"))
                    return AnimeSubtype.ONA;
                if (name.Contains("SPECIAL") || name.Contains("MOVIE"))
                    return AnimeSubtype.Special;
            }

            // Check release info for patterns
            if (meta.TryGetProperty("releaseInfo", out var releaseProp))
            {
                var release = releaseProp.GetString()?.ToUpperInvariant() ?? string.Empty;
                if (release.Contains("OVA"))
                    return AnimeSubtype.OVA;
                if (release.Contains("ONA"))
                    return AnimeSubtype.ONA;
                if (release.Contains("SPECIAL"))
                    return AnimeSubtype.Special;
            }

            return AnimeSubtype.Unknown;
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
