using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using InfiniteDrive.Models;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services.Scoring
{
    /// <summary>
    /// Implements bucket-based stream selection algorithm.
    /// Uses a (Resolution Tier × Source Tier) matrix with per-bucket caps
    /// to produce a curated list of stream options that includes quality variety
    /// (Remux, BluRay, WEB-DL) within each resolution tier.
    /// </summary>
    public class StreamScoringService : IStreamScoringService
    {
        private readonly ILogger _logger;
        private readonly int _maxStreams;
        private readonly List<(int Res, int SrcMax, int Max)> _buckets;

        /// <summary>
        /// Initializes a new instance of the StreamScoringService.
        /// </summary>
        /// <param name="logger">Logger instance for diagnostics.</param>
        /// <param name="config">Plugin configuration containing bucket settings.</param>
        public StreamScoringService(ILogger logger, PluginConfiguration config)
        {
            _logger   = logger;
            _maxStreams = Math.Clamp(config.MaxCuratedStreams, 1, 12);
            _buckets   = ParseBuckets(config.StreamBucketsJson);
        }

        /// <summary>
        /// Selects the best N candidates using the bucket algorithm.
        /// </summary>
        public List<StreamCandidate> SelectBest(List<StreamCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                return new List<StreamCandidate>();

            // Score every candidate
            var scored = candidates
                .Select(c => (
                    Candidate: c,
                    ResTier:   ResTier(c),
                    SrcTier:   SrcTier(c),
                    AudioTier: AudioTier(c),
                    Size:      c.FileSize ?? 0L
                ))
                .ToList();

            var selected  = new List<StreamCandidate>();
            var usedUrls  = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (res, srcMax, max) in _buckets)
            {
                var bucket = scored
                    .Where(x => x.ResTier == res && x.SrcTier <= srcMax)
                    .OrderBy(x  => x.SrcTier)
                    .ThenBy(x   => x.AudioTier)
                    .ThenByDescending(x => x.Size);

                var added = 0;
                foreach (var x in bucket)
                {
                    if (selected.Count >= _maxStreams) break;
                    var url = x.Candidate.Url ?? "";
                    if (!usedUrls.Add(url)) continue;

                    selected.Add(x.Candidate);
                    if (++added >= max) break;
                }

                if (selected.Count >= _maxStreams) break;
            }

            // Assign final display-order rank
            for (var i = 0; i < selected.Count; i++)
                selected[i].Rank = i;

            _logger.LogInformation(
                "[StreamScoringService] Selected {Count} from {Total} candidates",
                selected.Count, candidates.Count);

            return selected;
        }

        // ── Tier mappers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Maps resolution to tier: 0=4K 1=1080p 2=720p 3=480p 9=unknown.
        /// </summary>
        public static int ResTier(StreamCandidate c)
        {
            var r = c.QualityTier ?? "";
            var fn = c.FileName ?? "";

            if (r.Contains("remux", StringComparison.OrdinalIgnoreCase) ||
                fn.Contains("remux", StringComparison.OrdinalIgnoreCase)) return 0;
            if (r.Contains("2160", StringComparison.OrdinalIgnoreCase) ||
                fn.Contains("2160", StringComparison.OrdinalIgnoreCase) ||
                fn.Contains("4k", StringComparison.OrdinalIgnoreCase)) return 0;
            if (r.Contains("1080", StringComparison.OrdinalIgnoreCase) ||
                fn.Contains("1080", StringComparison.OrdinalIgnoreCase)) return 1;
            if (r.Contains("720", StringComparison.OrdinalIgnoreCase) ||
                fn.Contains("720", StringComparison.OrdinalIgnoreCase)) return 2;
            if (r.Contains("480", StringComparison.OrdinalIgnoreCase) ||
                fn.Contains("480", StringComparison.OrdinalIgnoreCase)) return 3;
            return 9;
        }

        /// <summary>
        /// Maps source type to tier: 0=Remux 1=BluRay 2=WEB-DL 3=WEB 9=unknown.
        /// </summary>
        public static int SrcTier(StreamCandidate c)
        {
            var fn = c.FileName ?? "";
            var fnUpper = fn.ToUpperInvariant();

            if (fnUpper.Contains("REMUX")) return 0;
            if (fnUpper.Contains("BLURAY") || fnUpper.Contains("BDRIP") ||
                fnUpper.Contains("BLU-RAY")) return 1;
            if (fnUpper.Contains("WEBDL") || fnUpper.Contains("WEB-DL")) return 2;
            if (fnUpper.Contains("WEB") || fnUpper.Contains("WEBRIP")) return 3;
            return 9;
        }

        /// <summary>
        /// Maps audio codec to tier: 0=Atmos 1=TrueHD 2=DTS-HD/DTS-X 3=DD+ 4=DTS 5=DD 6=AAC/FLAC/OPUS 9=unknown.
        /// </summary>
        public static int AudioTier(StreamCandidate c)
        {
            var fn = c.FileName ?? "";
            var fnUpper = fn.ToUpperInvariant();

            var has71 = fnUpper.Contains("7.1") || fnUpper.Contains("8 CHANNEL");
            var hasAtmos = fnUpper.Contains("ATMOS");

            // Atmos = TrueHD with 7.1 or explicit Atmos tag
            if (hasAtmos) return 0;

            if (fnUpper.Contains("TRUEHD")) return 1;
            if (fnUpper.Contains("DTS-HD") || fnUpper.Contains("DTSHD") ||
                fnUpper.Contains("DTS-X") || fnUpper.Contains("DTSX")) return 2;
            if (fnUpper.Contains("EAC3") || fnUpper.Contains("DDP") ||
                fnUpper.Contains("DD+") || fnUpper.Contains("DOLBY DIGITAL PLUS")) return 3;
            if (fnUpper.Contains("DTS")) return 4;
            if (fnUpper.Contains("AC3") || fnUpper.Contains("DD ") ||
                fnUpper.Contains("DOLBY DIGITAL")) return 5;
            if (fnUpper.Contains("AAC") || fnUpper.Contains("FLAC") ||
                fnUpper.Contains("OPUS")) return 6;
            return 9;
        }

        // ── Bucket config parsing ───────────────────────────────────────────────────

        /// <summary>
        /// Parses the JSON bucket configuration from PluginConfiguration.
        /// Falls back to default buckets if parsing fails.
        /// </summary>
        private static List<(int Res, int SrcMax, int Max)> ParseBuckets(string? json)
        {
            var defaults = new List<(int, int, int)>
            {
                (0, 0, 2), // 4K Remux: up to 2 streams (Atmos vs TrueHD variety)
                (0, 1, 1), // 4K BluRay: up to 1 stream
                (0, 2, 1), // 4K WEB-DL: up to 1 stream
                (1, 0, 1), // 1080p Remux: up to 1 stream
                (1, 1, 2), // 1080p BluRay: up to 2 streams (Atmos vs TrueHD)
                (2, 1, 1), // 720p (any src): up to 1 stream
            };

            if (string.IsNullOrWhiteSpace(json)) return defaults;

            try
            {
                var parsed = JsonSerializer.Deserialize<List<BucketDef>>(json);
                if (parsed != null && parsed.Count > 0)
                    return parsed.Select(b => (b.ResTier, b.SrcMax, b.MaxCount)).ToList();
            }
            catch { /* fall through to defaults */ }

            return defaults;
        }

        /// <summary>
        /// Internal class for JSON deserialization of bucket definitions.
        /// </summary>
        private class BucketDef
        {
            public int ResTier  { get; set; }
            public int SrcMax   { get; set; }
            public int MaxCount { get; set; }
        }
    }
}
