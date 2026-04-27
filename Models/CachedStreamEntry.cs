using System;
using System.Collections.Generic;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// Pre-cached stream metadata for a single media item (movie or episode).
    /// Stored in the <c>cached_streams</c> table. Populated proactively by
    /// <c>PreCacheAioStreamsTask</c> before users browse items.
    /// </summary>
    public class CachedStreamEntry
    {
        /// <summary>tmdb-{tmdbId}-movie | tmdb-{tmdbId}-s{s}e{e} | imdb-{imdbId}-movie (fallback)</summary>
        public string TmdbKey { get; set; } = string.Empty;

        /// <summary>IMDB identifier (tt1234567).</summary>
        public string ImdbId { get; set; } = string.Empty;

        /// <summary>movie | series</summary>
        public string MediaType { get; set; } = string.Empty;

        /// <summary>Season number; null for movies.</summary>
        public int? Season { get; set; }

        /// <summary>Episode number; null for movies.</summary>
        public int? Episode { get; set; }

        /// <summary>Emby GUID for fast lookup.</summary>
        public string? ItemId { get; set; }

        /// <summary>JSON array of <see cref="StreamVariant"/> objects.</summary>
        public string VariantsJson { get; set; } = "[]";

        /// <summary>UTC timestamp when cached.</summary>
        public string CachedAt { get; set; } = DateTime.UtcNow.ToString("o");

        /// <summary>UTC timestamp after which entry should be re-resolved.</summary>
        public string ExpiresAt { get; set; } = string.Empty;

        /// <summary>valid | expired | error</summary>
        public string Status { get; set; } = "valid";
    }

    /// <summary>
    /// One resolved stream variant within a <see cref="CachedStreamEntry"/>.
    /// Contains durable metadata (infoHash + fileIdx) that survives CDN URL rotation.
    /// </summary>
    public class StreamVariant
    {
        public string? InfoHash { get; set; }
        public int? FileIdx { get; set; }
        public string? FileName { get; set; }
        public string? Resolution { get; set; }
        public string? QualityTier { get; set; }
        public long? SizeBytes { get; set; }
        public int? Bitrate { get; set; }
        public string? VideoCodec { get; set; }
        public List<AudioStreamInfo>? AudioStreams { get; set; }
        public List<SubtitleStreamInfo>? SubtitleStreams { get; set; }
        public long? DurationMs { get; set; }
        public string? ProviderName { get; set; }
        public string? StreamType { get; set; }
        public string? SourceName { get; set; }
        public string? BingeGroup { get; set; }
        public string? StreamKey { get; set; }
        /// <summary>Direct CDN URL (may expire; used for immediate playback).</summary>
        public string? Url { get; set; }
        /// <summary>JSON-serialised HTTP headers needed for playback.</summary>
        public string? HeadersJson { get; set; }
    }

    public class AudioStreamInfo
    {
        public string? Language { get; set; }
        public string? Codec { get; set; }
        public int? Channels { get; set; }
        public bool IsDefault { get; set; }
    }

    public class SubtitleStreamInfo
    {
        public string? Language { get; set; }
        public bool IsDefault { get; set; }
    }

    /// <summary>
    /// Lightweight DTO returned by <c>GetUncachedItemsAsync</c>.
    /// Represents an item in media_items that has no cached_streams row.
    /// </summary>
    public class UncachedItem
    {
        public string ImdbId { get; set; } = string.Empty;
        public string? TmdbId { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public string Title { get; set; } = string.Empty;
    }
}
