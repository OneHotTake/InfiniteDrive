using System;
using System.Collections.Generic;

namespace EmbyStreams.Models
{
    /// <summary>
    /// A value type representing a media identifier with provider type and value.
    /// Format: "type:value" (e.g., "imdb:tt123456", "tmdb:1160419").
    /// </summary>
    public readonly struct MediaId : IEquatable<MediaId>
    {
        /// <summary>
        /// The provider type (IMDb, TMDB, TVDB, etc.).
        /// </summary>
        public MediaIdType Type { get; }

        /// <summary>
        /// The provider-specific ID value (e.g., "tt123456" for IMDb, "1160419" for TMDB).
        /// </summary>
        public string Value { get; }

        /// <summary>
        /// Creates a new MediaId.
        /// </summary>
        /// <param name="type">The provider type.</param>
        /// <param name="value">The provider-specific ID value.</param>
        /// <exception cref="ArgumentException">Thrown when value is null or empty.</exception>
        public MediaId(MediaIdType type, string value)
        {
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException("MediaId value cannot be null or empty", nameof(value));

            Type = type;
            Value = value;
        }

        /// <summary>
        /// Parses a string in "type:value" format into a MediaId.
        /// </summary>
        /// <param name="input">The string to parse (e.g., "imdb:tt123456").</param>
        /// <returns>A MediaId instance.</returns>
        /// <exception cref="ArgumentException">Thrown when input format is invalid.</exception>
        public static MediaId Parse(string input)
        {
            if (string.IsNullOrEmpty(input))
                throw new ArgumentException("MediaId cannot be empty", nameof(input));

            var parts = input.Split(':', 2);
            if (parts.Length != 2)
                throw new ArgumentException($"Invalid MediaId format: {input}. Expected 'type:value'.", nameof(input));

            if (string.IsNullOrEmpty(parts[0]))
                throw new ArgumentException($"MediaId type cannot be empty: {input}", nameof(input));

            if (string.IsNullOrEmpty(parts[1]))
                throw new ArgumentException($"MediaId value cannot be empty: {input}", nameof(input));

            var type = MediaIdTypeExtensions.Parse(parts[0]);
            return new MediaId(type, parts[1]);
        }

        /// <summary>
        /// Tries to parse a string in "type:value" format.
        /// </summary>
        /// <param name="input">The string to parse.</param>
        /// <param name="mediaId">The parsed MediaId, or default if parsing fails.</param>
        /// <returns>True if parsing succeeded, false otherwise.</returns>
        public static bool TryParse(string input, out MediaId mediaId)
        {
            mediaId = default;
            if (string.IsNullOrEmpty(input))
                return false;

            var parts = input.Split(':', 2);
            if (parts.Length != 2)
                return false;

            if (string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
                return false;

            try
            {
                var type = MediaIdTypeExtensions.Parse(parts[0]);
                mediaId = new MediaId(type, parts[1]);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Implicit conversion from string to MediaId.
        /// </summary>
        public static implicit operator MediaId(string value) => Parse(value);

        /// <summary>
        /// Checks equality with another MediaId.
        /// </summary>
        public bool Equals(MediaId other)
        {
            return Type == other.Type && Value == other.Value;
        }

        /// <summary>
        /// Checks equality with another object.
        /// </summary>
        public override bool Equals(object? obj)
        {
            return obj is MediaId other && Equals(other);
        }

        /// <summary>
        /// Returns the hash code for this MediaId.
        /// </summary>
        public override int GetHashCode()
        {
            return HashCode.Combine(Type.GetHashCode(), Value.GetHashCode());
        }

        /// <summary>
        /// Equality operator.
        /// </summary>
        public static bool operator ==(MediaId left, MediaId right)
        {
            return left.Equals(right);
        }

        /// <summary>
        /// Inequality operator.
        /// </summary>
        public static bool operator !=(MediaId left, MediaId right)
        {
            return !(left == right);
        }

        /// <summary>
        /// Returns the string representation "type:value".
        /// </summary>
        public override string ToString()
        {
            return $"{Type.ToLowerString()}:{Value}";
        }
    }
}
