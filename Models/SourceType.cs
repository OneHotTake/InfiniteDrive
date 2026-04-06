using System;

namespace EmbyStreams.Models
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

        /// <summary>Trakt list or watchlist source.</summary>
        Trakt,

        /// <summary>MDBList list source.</summary>
        MdbList
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
                SourceType.Trakt   => "Trakt",
                SourceType.MdbList => "MDBList",
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
                "trakt"                => SourceType.Trakt,
                "mdblist"              => SourceType.MdbList,
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
                SourceType.Trakt   => "trakt",
                SourceType.MdbList => "mdblist",
                _                 => "unknown"
            };
        }
    }
}
