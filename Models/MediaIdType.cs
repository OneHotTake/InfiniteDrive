using System;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// Supported external provider types for media identification.
    /// Used by the MediaId system to distinguish between different provider ID formats.
    /// </summary>
    public enum MediaIdType
    {
        /// <summary>The Movie Database (TMDB) ID.</summary>
        Tmdb,

        /// <summary>Internet Movie Database (IMDb) ID.</summary>
        Imdb,

        /// <summary>The TV Database (TVDB) ID.</summary>
        Tvdb,

        /// <summary>AniList database ID.</summary>
        AniList,

        /// <summary>AniDB database ID.</summary>
        AniDB,

        /// <summary>Kitsu database ID.</summary>
        Kitsu
    }

    /// <summary>
    /// Extension methods for MediaIdType.
    /// </summary>
    public static class MediaIdTypeExtensions
    {
        /// <summary>
        /// Parses a lowercase string into a MediaIdType.
        /// </summary>
        /// <param name="value">The string value to parse (e.g., "imdb", "tmdb").</param>
        /// <returns>The corresponding MediaIdType.</returns>
        /// <exception cref="ArgumentException">Thrown when the string cannot be parsed.</exception>
        public static MediaIdType Parse(string value)
        {
            return value.ToLowerInvariant() switch
            {
                "tmdb" => MediaIdType.Tmdb,
                "imdb" => MediaIdType.Imdb,
                "tvdb" => MediaIdType.Tvdb,
                "anilist" => MediaIdType.AniList,
                "anidb" => MediaIdType.AniDB,
                "kitsu" => MediaIdType.Kitsu,
                _ => throw new ArgumentException($"Unknown MediaIdType: {value}", nameof(value))
            };
        }

        /// <summary>
        /// Returns the lowercase string representation of the MediaIdType.
        /// </summary>
        public static string ToLowerString(this MediaIdType type)
        {
            return type.ToString().ToLowerInvariant();
        }
    }
}
