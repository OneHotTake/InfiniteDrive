using System;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Parses and validates stream IDs from various provider formats.
    /// Sprint 100B-10: Unknown provider edge case.
    /// Handles: tt{imdbid}, kitsu:{id}, anilist:{id}, tmdb:{id}, mal:{id}.
    /// </summary>
    public static class StreamIdParser
    {
        /// <summary>
        /// Parses a stream ID and extracts provider information.
        /// Formats:
        /// - tt{number} → IMDB (provider: "imdb", id: tt{number})
        /// - kitsu:{number} → Kitsu (provider: "kitsu", id: {number})
        /// - anilist:{number} → AniList (provider: "anilist", id: {number})
        /// - tmdb:{number} → TMDB (provider: "tmdb", id: {number})
        /// - mal:{number} → MyAnimeList (provider: "mal", id: {number})
        /// - {unknown}:{id} → Unknown provider (provider: "unknown_{prefix}", id: {prefix}:{id})
        /// </summary>
        public static (string provider, string id, bool isKnown) ParseStreamId(
            string? streamId,
            ILogger? logger = null)
        {
            if (string.IsNullOrWhiteSpace(streamId))
                return ("", "", false);

            var id = streamId!;

            // Check for prefix: separator format
            var colonIndex = id.IndexOf(':');
            if (colonIndex > 0)
            {
                var prefix = id.Substring(0, colonIndex).ToLowerInvariant();
                var value = id.Substring(colonIndex + 1);

                switch (prefix)
                {
                    case "imdb":
                        // tt123456 format
                        if (value.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                            return ("imdb", value, true);
                        break;

                    case "kitsu":
                        return ("kitsu", value, true);

                    case "anilist":
                    case "anilist_id":
                        return ("anilist", value, true);

                    case "tmdb":
                        return ("tmdb", value, true);

                    case "mal":
                    case "mal_id":
                        return ("mal", value, true);

                    default:
                        // Unknown provider
                        var normalizedProvider = $"unknown_{prefix}";
                        logger?.LogWarning(
                            "[EmbyStreams] Unknown stream ID prefix '{Prefix}' - treating as {Provider}",
                            prefix, normalizedProvider);
                        return (normalizedProvider, id, false);
                }
            }

            // Check for tt-prefixed IMDB ID (no colon)
            if (id.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                // Verify it's a valid tt-prefixed ID
                if (id.Length > 2 && long.TryParse(id.Substring(2), out _))
                {
                    return ("imdb", id, true);
                }
            }

            // Plain number - assume IMDB but log for verification
            if (long.TryParse(id, out _))
            {
                logger?.LogDebug(
                    "[EmbyStreams] Numeric ID without prefix detected - treating as IMDB: {Id}",
                    id);
                return ("imdb", id, true);
            }

            // Unknown format - attempt with raw ID
            logger?.LogWarning(
                "[EmbyStreams] Unknown stream ID format: {Id} - attempting with raw ID",
                id);
            return ("unknown", id, false);
        }

        /// <summary>
        /// Normalizes a stream ID to standard IMDB format if possible.
        /// For anime providers (kitsu, anilist, mal), returns the ID as-is.
        /// For IMDB IDs, ensures tt prefix.
        /// </summary>
        public static string NormalizeToImdbId(string streamId, ILogger? logger = null)
        {
            var (provider, id, _) = ParseStreamId(streamId, logger);

            if (provider == "imdb")
                return id;

            // For non-IMDB providers, return the prefixed ID
            if (provider != "unknown")
            {
                return $"{provider}:{id}";
            }

            // Unknown format - return as-is
            return streamId ?? string.Empty;
        }
    }
}
