using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using InfiniteDrive.Models;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Builds HLS variant playlists for stream resolution.
    /// Maps AIOStreams streams to quality tiers and generates M3U8 manifests.
    /// </summary>
    public class M3u8Builder
    {
        public const string MimeType = "application/vnd.apple.mpegurl";
        public const string Version = "#EXTM3U";

        /// <summary>
        /// Quality tier metadata for stream classification.
        /// </summary>
        public static readonly Dictionary<string, TierMetadata> TierMetadata = new()
        {
            ["4k_hdr"] = new TierMetadata { DisplayName = "4K HDR", Resolution = "2160p", Is4K = true, IsHDR = true },
            ["4k_sdr"] = new TierMetadata { DisplayName = "4K SDR", Resolution = "2160p", Is4K = true, IsHDR = false },
            ["hd_broad"] = new TierMetadata { DisplayName = "1080p", Resolution = "1080p", Is4K = false, IsHDR = false },
            ["sd_broad"] = new TierMetadata { DisplayName = "720p", Resolution = "480p-720p", Is4K = false, IsHDR = false },
        };

        /// <summary>
        /// Quality tiers in priority order (highest to lowest).
        /// </summary>
        public static readonly string[] TierPriority = new[] { "4k_hdr", "4k_sdr", "hd_broad", "sd_broad" };

        /// <summary>
        /// Maps an AIOStreams stream to its quality tier.
        /// </summary>
        public static string MapStreamToTier(AioStreamsStream stream)
        {
            if (stream.ParsedFile?.Resolution != null)
            {
                var resolution = stream.ParsedFile.Resolution.ToLowerInvariant();
                var isHdr = stream.ParsedFile.VisualTags?.Any(t =>
                    t.Contains("HDR", StringComparison.OrdinalIgnoreCase) ||
                    t.Contains("DV", StringComparison.OrdinalIgnoreCase)) ?? false;

                if (resolution.Contains("4k") || resolution.Contains("2160"))
                {
                    return isHdr ? "4k_hdr" : "4k_sdr";
                }
                if (resolution.Contains("1080"))
                {
                    return "hd_broad";
                }
            }

            return "sd_broad";
        }

        /// <summary>
        /// Extracts source name from AIOStreams stream.
        /// </summary>
        public static string GetSourceName(AioStreamsStream stream)
        {
            if (!string.IsNullOrEmpty(stream.Addon))
            {
                return stream.Addon;
            }

            if (!string.IsNullOrEmpty(stream.Title))
            {
                var parts = stream.Title.Split(new[] { ' ', '-', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
                return parts.FirstOrDefault() ?? "unknown";
            }

            return "unknown";
        }

        /// <summary>
        /// Determines if stream uses HEVC codec.
        /// </summary>
        public static bool IsHevcStream(AioStreamsStream stream)
        {
            if (stream.ParsedFile?.Encode != null)
            {
                return stream.ParsedFile.Encode.Contains("265", StringComparison.OrdinalIgnoreCase) ||
                       stream.ParsedFile.Encode.Contains("HEVC", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        /// <summary>
        /// Creates an HLS variant playlist from stream variants.
        /// </summary>
        /// <param name="baseUrl">Emby base URL for constructing stream URLs.</param>
        /// <param name="quality">Requested quality tier.</param>
        /// <param name="variants">List of stream variants to include.</param>
        /// <returns>M3U8 manifest as string.</returns>
        public string CreateVariantPlaylist(
            string baseUrl,
            string quality,
            List<M3U8Variant> variants)
        {
            if (variants.Count == 0)
            {
                return CreateEmptyPlaylist();
            }

            var sb = new StringBuilder();
            sb.AppendLine(Version);
            sb.AppendLine($"#EXT-X-VERSION:3");
            sb.AppendLine($"#EXT-X-TARGETDURATION:99999");
            sb.AppendLine($"#EXT-X-MEDIA-SEQUENCE:0");
            sb.AppendLine($"#EXT-X-PLAYLIST-TYPE:VOD");

            var metadata = TierMetadata.GetValueOrDefault(quality, TierMetadata["sd_broad"]);

            // Sort variants by bandwidth descending (HLS spec: highest quality first)
            var sortedVariants = variants
                .OrderByDescending(v => v.Bandwidth)
                .ThenBy(v => v.DisplayName)
                .ToList();

            foreach (var variant in sortedVariants)
            {
                var bandwidth = variant.Bandwidth > 0 ? variant.Bandwidth : 5000000;
                var resolution = string.IsNullOrEmpty(variant.Resolution) ? metadata.Resolution : variant.Resolution;

                sb.AppendLine($"#EXT-X-STREAM-INF:BANDWIDTH={bandwidth},RESOLUTION={resolution},NAME=\"{variant.DisplayName}\"");
                sb.AppendLine(variant.Url);
            }

            sb.AppendLine("#EXT-X-ENDLIST");
            return sb.ToString();
        }

        /// <summary>
        /// Creates an empty M3U8 playlist when no streams are available.
        /// </summary>
        private static string CreateEmptyPlaylist()
        {
            return string.Join("\n",
                "#EXTM3U",
                "#EXT-X-VERSION:3",
                "#EXT-X-PLAYLIST-TYPE:VOD",
                "#EXT-X-ENDLIST");
        }
    }

    /// <summary>
    /// Quality tier metadata.
    /// </summary>
    public class TierMetadata
    {
        public string DisplayName { get; set; } = string.Empty;
        public string Resolution { get; set; } = string.Empty;
        public bool Is4K { get; set; }
        public bool IsHDR { get; set; }
    }

    /// <summary>
    /// HLS variant stream representation.
    /// </summary>
    public class M3U8Variant
    {
        /// <summary>
        /// Display name for the variant.
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Source name (e.g., "torrentio", "mediafusion").
        /// </summary>
        public string SourceName { get; set; } = string.Empty;

        /// <summary>
        /// Stream URL (may be signed).
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Estimated bandwidth in bits per second.
        /// </summary>
        public long Bandwidth { get; set; }

        /// <summary>
        /// Video resolution (e.g., "1920x1080").
        /// </summary>
        public string Resolution { get; set; } = string.Empty;

        /// <summary>
        /// Whether this stream uses HEVC codec.
        /// </summary>
        public bool IsHevc { get; set; }

        /// <summary>
        /// Whether this stream has HDR.
        /// </summary>
        public bool IsHdr { get; set; }
    }
}
