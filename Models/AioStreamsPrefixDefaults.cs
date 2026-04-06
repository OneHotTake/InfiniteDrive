using System;
using System.Collections.Generic;

namespace EmbyStreams.Models
{
    /// <summary>
    /// Default prefix mappings for AIOStreams media ID types.
    /// Used to map MediaIdType values to their corresponding AIOStreams
    /// URL prefixes (e.g., "tmdb:12345" → "tmdb/12345" in AIOStreams API).
    /// </summary>
    public static class AioStreamsPrefixDefaults
    {
        /// <summary>
        /// Default prefix map for all supported MediaIdTypes.
        /// </summary>
        public static readonly IReadOnlyDictionary<MediaIdType, string> DefaultPrefixMap =
            new Dictionary<MediaIdType, string>
            {
                { MediaIdType.Tmdb,    "tmdb"    },
                { MediaIdType.Imdb,    "imdb"    },
                { MediaIdType.Tvdb,    "tvdb"    },
                { MediaIdType.AniList, "anilist" },
                { MediaIdType.AniDB,   "anidb"   },
                { MediaIdType.Kitsu,   "kitsu"   },
            };

        /// <summary>
        /// Gets the AIOStreams prefix for a given MediaIdType.
        /// </summary>
        /// <param name="type">The media ID type.</param>
        /// <returns>The AIOStreams prefix.</returns>
        /// <exception cref="KeyNotFoundException">Thrown when the type is not in the default map.</exception>
        public static string GetPrefix(MediaIdType type)
        {
            return DefaultPrefixMap[type];
        }

        /// <summary>
        /// Tries to get the AIOStreams prefix for a given MediaIdType.
        /// </summary>
        /// <param name="type">The media ID type.</param>
        /// <param name="prefix">The output prefix if found.</param>
        /// <returns>True if the prefix was found, false otherwise.</returns>
        public static bool TryGetPrefix(MediaIdType type, out string prefix)
        {
            return DefaultPrefixMap.TryGetValue(type, out prefix);
        }

        /// <summary>
        /// Formats a MediaId into an AIOStreams URL path segment.
        /// </summary>
        /// <param name="mediaId">The media ID to format.</param>
        /// <returns>The AIOStreams path segment (e.g., "tmdb/1160419").</returns>
        public static string ToAioStreamsPath(MediaId mediaId)
        {
            var prefix = GetPrefix(mediaId.Type);
            return $"{prefix}/{mediaId.Value}";
        }
    }
}
