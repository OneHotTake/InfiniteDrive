using System;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// Type of source for catalog content.
    /// Determines how the source is discovered and synced.
    /// </summary>
    public enum SourceType
    {
        /// <summary>Built-in source (e.g., AIOStreams).</summary>
        BuiltIn,

        /// <summary>AIOStreams manifest source.</summary>
        Aio,

        /// <summary>
        /// User-supplied public RSS feed (Trakt or MDBList).
        /// Replaces the phantom Trakt and MdbList enum values removed in Sprint 158.
        /// The service field on user_catalogs distinguishes trakt vs mdblist.
        /// </summary>
        UserRss
    }

    /// <summary>
    /// Extension methods for SourceType.
    /// </summary>
    public static class SourceTypeExtensions
    {
        /// <summary>
        /// Returns a user-friendly display string for this source type.
        /// </summary>
        public static string ToDisplayString(this SourceType type)
        {
            return type switch
            {
                SourceType.BuiltIn => "Built-in",
                SourceType.Aio     => "AIOStreams",
                SourceType.UserRss => "User RSS",
                _                 => "Unknown"
            };
        }

        /// <summary>
        /// Parses a lowercase string into a SourceType.
        /// </summary>
        public static SourceType Parse(string value)
        {
            return value.ToLowerInvariant() switch
            {
                "builtin" or "built-in" => SourceType.BuiltIn,
                "aio" or "aiostreams"   => SourceType.Aio,
                "user_rss" or "userrss" => SourceType.UserRss,
                // Legacy values migrated in Sprint 158 — map to UserRss
                "trakt" or "mdblist"    => SourceType.UserRss,
                _                      => throw new ArgumentException($"Unknown SourceType: {value}", nameof(value))
            };
        }

        /// <summary>
        /// Returns the lowercase string representation of the SourceType.
        /// </summary>
        public static string ToLowerString(this SourceType type)
        {
            return type switch
            {
                SourceType.BuiltIn => "builtin",
                SourceType.Aio     => "aio",
                SourceType.UserRss => "user_rss",
                _                 => "unknown"
            };
        }
    }
}
