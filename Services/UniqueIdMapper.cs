using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Centralized mapping of Stremio/AIOStreams provider prefixes to Emby NFO
    /// uniqueid type attribute values.
    ///
    /// Sprint 101A-01: UniqueID type attribute audit.
    /// This class provides the single source of truth for all uniqueid type
    /// mappings across the codebase.
    /// </summary>
    public static class UniqueIdMapper
    {
        // ── Provider to NFO Type Attribute Mapping ─────────────────────

        /// <summary>
        /// Maps a Stremio provider prefix to the exact NFO uniqueid
        /// type attribute string value.
        /// </summary>
        /// <param name="providerPrefix">Provider prefix from stream ID or uniqueid field name.</param>
        /// <returns>The exact type attribute value, or null if provider is unknown.</returns>
        public static string? MapProviderToNfoType(string providerPrefix)
        {
            if (string.IsNullOrEmpty(providerPrefix))
                return null;

            var lower = providerPrefix.ToLowerInvariant();

            // IMDB ID formats
            if (lower == "imdb" || lower == "imdb_id")
                return "Imdb";

            // TMDB ID formats
            if (lower == "tmdb" || lower == "tmdb_id")
                return "Tmdb";

            // AniList ID formats (primary for anime)
            if (lower == "anilist" || lower == "anilist_id" || lower == "anilist_id:")
                return "AniList";

            // Kitsu ID formats (primary for anime)
            if (lower == "kitsu" || lower == "kitsu_id" || lower == "kitsu_id:")
                return "Kitsu";

            // MyAnimeList ID formats
            if (lower == "mal" || lower == "mal_id")
                return "MyAnimeList";

            // AniDB ID formats (alternative for anime)
            if (lower == "anidb" || lower == "anidb_id")
                return "AniDB";

            // Unknown provider - do not write a uniqueid element
            return null;
        }

        /// <summary>
        /// Returns whether a provider prefix is recognized as a known
        /// anime provider that should generate a uniqueid element.
        /// </summary>
        /// <param name="providerPrefix">Provider prefix to check.</param>
        /// <returns>True if the provider is recognized for uniqueid generation.</returns>
        public static bool IsRecognizedAnimeProvider(string providerPrefix)
        {
            var lower = providerPrefix.ToLowerInvariant();

            // Primary anime providers
            if (lower == "anilist" || lower == "anilist_id" || lower == "anilist_id:")
                return true;
            if (lower == "kitsu" || lower == "kitsu_id" || lower == "kitsu_id:")
                return true;
            if (lower == "mal" || lower == "mal_id" || lower == "mal_id:")
                return true;
            if (lower == "anidb" || lower == "anidb_id")
                return true;

            // TMDB and IMDB are also valid for anime items
            // but these are secondary sources, primary is above
            if (lower == "tmdb" || lower == "tmdb_id")
                return true;
            if (lower == "imdb" || lower == "imdb_id")
                return true;

            return false;
        }

        /// <summary>
        /// Extracts the provider prefix from a stream ID string.
        /// Supports formats: provider:id (e.g., "kitsu:10049") and plain IDs
        /// (e.g., "anidb:12345").
        /// </summary>
        /// <param name="streamId">Stream ID or ID string to parse.</param>
        /// <returns>Tuple of (providerPrefix, idValue).</returns>
        public static (string Provider, string IdValue) ParseStreamId(string? streamId)
        {
            if (string.IsNullOrEmpty(streamId))
                return (string.Empty, string.Empty);

            // Check for colon separator format (provider:id)
            var colonIndex = streamId!.IndexOf(':');
            if (colonIndex > 0)
            {
                var prefix = streamId!.Substring(0, colonIndex).ToLowerInvariant();
                var idValue = streamId!.Substring(colonIndex + 1);
                return (prefix, idValue);
            }

            // Check for AniDB format (anidb:12345)
            if (streamId.StartsWith("anidb:", StringComparison.OrdinalIgnoreCase))
            {
                return ("anidb", streamId!.Substring(7));
            }

            // Check for tt-prefixed IMDB
            if (streamId.StartsWith("tt", StringComparison.OrdinalIgnoreCase) && streamId.Length > 2)
            {
                // Verify it's all digits after "tt"
                var isAllDigits = streamId!.Substring(2).All(char.IsDigit);
                if (isAllDigits)
                    return ("imdb", streamId!);
            }

            // Plain number - assume IMDB
            if (long.TryParse(streamId, out _))
            {
                return ("imdb", streamId!);
            }

            // Unknown format
            return (string.Empty, streamId!);
        }

        /// <summary>
        /// Normalizes an ID to the preferred format for NFO output.
        /// Priority for Emby's Anime plugin:
        /// 1. AniDB (anidb:12345) - primary source
        /// 2. AniList (anilist:12345) - secondary
        /// 3. Kitsu (kitsu:10049) - secondary
        /// 4. MAL (mal:12345) - secondary
        /// 5. IMDB (tt1234567) - fallback
        /// </summary>
        /// <param name="provider">Provider type from MapProviderToNfoType.</param>
        /// <param name="idValue">The ID value.</param>
        /// <returns>The provider:id format string for the primary provider.</returns>
        public static string NormalizeAnimeId(string provider, string idValue)
        {
            var lower = provider.ToLowerInvariant();

            if (lower == "anidb" || lower == "anidb_id")
                return $"anidb:{idValue}";
            if (lower == "anilist" || lower == "anilist_id" || lower == "anilist_id:")
                return $"anilist:{idValue}";
            if (lower == "kitsu" || lower == "kitsu_id" || lower == "kitsu_id:")
                return $"kitsu:{idValue}";
            if (lower == "mal" || lower == "mal_id")
                return $"mal:{idValue}";

            // IMDB as fallback for items without anime-specific IDs
            return idValue.StartsWith("tt") ? idValue : $"tt{idValue}";
        }

        /// <summary>
        /// Gets all recognized anime provider prefixes.
        /// Used for logging and validation.
        /// </summary>
        /// <returns>Array of all recognized anime provider prefixes.</returns>
        public static string[] GetRecognizedAnimeProviders()
        {
            return new[]
            {
                "anidb", "anilist", "anilist_id",
                "kitsu", "kitsu_id", "kitsu_id:",
                "mal", "mal_id", "mal_id:"
            };
        }
    }
}
