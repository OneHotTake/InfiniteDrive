using System;
using System.Collections.Generic;
using System.Linq;
using InfiniteDrive.Models;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Selects the best multi-version streams for .strm file writing.
    /// Two-phase algorithm:
    ///   Phase 1: Match desired quality buckets in order (bucket priority).
    ///   Phase 2: Fill remaining slots with next-best unmatched streams.
    /// </summary>
    public static class VersionSelectorService
    {
        /// <summary>
        /// Selects up to <paramref name="hardCap"/> versions from the ranked stream pool,
        /// guided by <paramref name="desiredBuckets"/>.
        /// </summary>
        /// <param name="rankedStreams">Streams pre-sorted by RankScore descending.</param>
        /// <param name="desiredBuckets">Ordered quality preferences.</param>
        /// <param name="hardCap">Maximum versions to select (default 8).</param>
        public static List<SelectedVersion> SelectBestVersions(
            List<ParsedStream> rankedStreams,
            List<DesiredVersionBucket> desiredBuckets,
            int hardCap = 8)
        {
            if (rankedStreams.Count == 0) return new();

            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<SelectedVersion>();

            // ── Phase 1: Bucket matching (ordered priority) ─────────────────────
            foreach (var bucket in desiredBuckets)
            {
                if (results.Count >= hardCap) break;
                var matchCount = 0;

                foreach (var stream in rankedStreams)
                {
                    if (results.Count >= hardCap) break;
                    if (matchCount >= bucket.Count) break;
                    if (claimed.Contains(stream.Url)) continue;

                    if (MatchesBucket(stream, bucket))
                    {
                        claimed.Add(stream.Url);
                        results.Add(MakeVersion(stream, BucketLabel(bucket)));
                        matchCount++;
                    }
                }
            }

            // ── Phase 2: Fill remaining with next-best unmatched ────────────────
            foreach (var stream in rankedStreams)
            {
                if (results.Count >= hardCap) break;
                if (claimed.Contains(stream.Url)) continue;

                claimed.Add(stream.Url);
                results.Add(MakeVersion(stream, ""));
            }

            return results;
        }

        /// <summary>
        /// Compares two selections and returns true if the new one is meaningfully
        /// different (different stream keys or higher total score).
        /// </summary>
        public static bool ShouldReplace(
            List<SelectedVersion> current, List<SelectedVersion> incoming)
        {
            if (current.Count == 0) return incoming.Count > 0;
            if (incoming.Count == 0) return false;

            // Different stream set → replace
            var currentKeys = new HashSet<string?>(
                current.Select(v => v.Stream.StreamKey), StringComparer.OrdinalIgnoreCase);
            var incomingKeys = new HashSet<string?>(
                incoming.Select(v => v.Stream.StreamKey), StringComparer.OrdinalIgnoreCase);

            if (!currentKeys.SetEquals(incomingKeys))
                return true;

            // Same set but higher total score → replace
            var currentScore = current.Sum(v => v.SelectedScore);
            var incomingScore = incoming.Sum(v => v.SelectedScore);
            return incomingScore > currentScore;
        }

        // ── Bucket matching ────────────────────────────────────────────────────

        private static bool MatchesBucket(ParsedStream stream, DesiredVersionBucket bucket)
        {
            if (!ResolutionMatches(stream.Resolution, bucket.Resolution))
                return false;

            if (!string.IsNullOrEmpty(bucket.Audio) &&
                !bucket.Audio.Equals("Any Audio", StringComparison.OrdinalIgnoreCase) &&
                !bucket.Audio.Equals("Any", StringComparison.OrdinalIgnoreCase))
            {
                if (!AudioMatches(stream.AudioGroup, bucket.Audio))
                    return false;
            }

            return true;
        }

        private static bool ResolutionMatches(string streamRes, string bucketRes)
        {
            if (string.IsNullOrEmpty(bucketRes) ||
                bucketRes.Equals("Any", StringComparison.OrdinalIgnoreCase))
                return true;

            // Normalise both sides
            var s = NormaliseForMatch(streamRes);
            var b = NormaliseForMatch(bucketRes);
            return s.Contains(b) || b.Contains(s);
        }

        private static bool AudioMatches(string streamAudio, string bucketAudio)
        {
            // Exact match
            if (streamAudio.Equals(bucketAudio, StringComparison.OrdinalIgnoreCase))
                return true;

            // Bucket "5.1/7.1 (Surround)" matches "Lossless/Premium" (lossless is better)
            if (bucketAudio.Contains("5.1") || bucketAudio.Contains("7.1") || bucketAudio.Contains("Surround"))
            {
                if (streamAudio.Equals("Lossless/Premium", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (streamAudio.Contains("5.1") || streamAudio.Contains("Surround"))
                    return true;
            }

            // Bucket "DD/DTS" matches better audio groups too
            if (bucketAudio.Contains("DD") || bucketAudio.Contains("DTS") || bucketAudio.Contains("Compressed"))
            {
                if (streamAudio.Equals("Lossless/Premium", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (streamAudio.Contains("DD/DTS") || streamAudio.Contains("Surround"))
                    return true;
            }

            return false;
        }

        private static string NormaliseForMatch(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace(" ", "").ToLowerInvariant();
        }

        // ── Version building ───────────────────────────────────────────────────

        private static SelectedVersion MakeVersion(ParsedStream stream, string bucketLabel)
        {
            var sizePart = stream.SizeGiB > 0 ? $"{stream.SizeGiB:F1}GiB" : "";
            var audioPart = string.IsNullOrEmpty(stream.AudioPretty) || stream.AudioPretty == "Unknown Audio"
                ? ""
                : stream.AudioPretty;
            var resPart = stream.Resolution == "Unknown" ? "" : stream.Resolution;

            var parts = new[] { resPart, audioPart, sizePart }
                .Where(p => !string.IsNullOrEmpty(p));
            var label = string.Join(" - ", parts);

            return new SelectedVersion
            {
                Stream = stream,
                VersionLabel = label,
                SelectedScore = stream.RankScore,
                MatchedBucket = bucketLabel,
            };
        }

        private static string BucketLabel(DesiredVersionBucket bucket)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(bucket.Resolution)) parts.Add(bucket.Resolution);
            if (!string.IsNullOrEmpty(bucket.Audio) &&
                !bucket.Audio.Equals("Any Audio", StringComparison.OrdinalIgnoreCase))
                parts.Add(bucket.Audio);
            return string.Join(" + ", parts);
        }
    }
}
