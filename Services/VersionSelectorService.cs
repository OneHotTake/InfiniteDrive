using System;
using System.Collections.Generic;
using System.Linq;
using InfiniteDrive;
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
            int hardCap = 8,
            PluginConfiguration? config = null)
        {
            if (rankedStreams.Count == 0) return new();

            rankedStreams = FilterEligibleStreams(rankedStreams, config);
            if (rankedStreams.Count == 0) return new();

            hardCap = Math.Min(RuntimePolicy.EmbyVersionLimit, Math.Max(1, hardCap));
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<SelectedVersion>();

            // Preserve materially distinct editions before using remaining slots
            // for quality variants. AIOStreams remains the ranking/filter authority.
            // Owned/library candidates win within each edition.
            var editionRepresentatives = rankedStreams
                .OrderByDescending(s => s.IsLibrary)
                .ThenByDescending(s => s.RankScore)
                .GroupBy(EditionKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(s => EditionKey(s) == "standard" ? 0 : 1)
                .ThenByDescending(s => s.IsLibrary)
                .ThenByDescending(s => s.RankScore);

            foreach (var stream in editionRepresentatives)
            {
                if (results.Count >= hardCap) break;
                if (!claimed.Add(stream.Url)) continue;
                results.Add(MakeVersion(stream, "edition"));
            }

            // ── Phase 1: Bucket matching (ordered priority) ─────────────────────
            foreach (var bucket in desiredBuckets)
            {
                if (results.Count >= hardCap) break;
                var bucketLabel = BucketLabel(bucket);
                var matchCount = 0;
                foreach (var stream in rankedStreams)
                {
                    if (results.Count >= hardCap) break;
                    if (matchCount >= bucket.Count) break;
                    if (claimed.Contains(stream.Url)) continue;

                    if (!MatchesBucket(stream, bucket)) continue;
                    claimed.Add(stream.Url);
                    results.Add(MakeVersion(stream, bucketLabel));
                    matchCount++;
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
        /// Applies release safety policy before both primary and fallback selection.
        /// InfiniteDrive enforces the small set of playback-safety invariants that
        /// must not be bypassed by refresh or fallback: no CAM/TS and no REMUX.
        /// Remaining quality/ranking policy comes from AIOStreams.
        /// </summary>
        public static List<ParsedStream> FilterEligibleStreams(
            IEnumerable<ParsedStream> streams,
            PluginConfiguration? config) =>
            streams
                .Where(s => !string.IsNullOrWhiteSpace(s.Url))
                .Where(s => config?.AllowCam == true
                    || !string.Equals(s.SourceTag, "CAM/TS", StringComparison.OrdinalIgnoreCase))
                .Where(s => config?.AllowRemux == true
                    || !s.SourceTag.Contains("remux", StringComparison.OrdinalIgnoreCase))
                .GroupBy(s => s.Url, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

        /// <summary>
        /// Compares stored versions against a newly proposed selection.
        /// Works directly with <see cref="StoredVersion"/> from the database —
        /// no reconstruction needed.
        /// </summary>
        public static bool ShouldReplace(
            List<StoredVersion> current,
            List<SelectedVersion> proposed)
        {
            if (proposed == null || proposed.Count == 0) return false;
            if (current == null || current.Count == 0) return true;

            // More versions available → upgrade
            if (proposed.Count > current.Count) return true;

            // Different stream set → upgrade (new sources found)
            var currentKeys = new HashSet<string?>(
                current.Select(v => v.StreamKey), StringComparer.OrdinalIgnoreCase);
            var proposedKeys = new HashSet<string?>(
                proposed.Select(v => v.Stream.StreamKey), StringComparer.OrdinalIgnoreCase);
            if (!currentKeys.SetEquals(proposedKeys))
                return true;

            // 15% total score improvement threshold
            var currentTotalScore = current.Sum(v => v.RankScore);
            var proposedTotalScore = proposed.Sum(v => v.SelectedScore);
            if (proposedTotalScore > currentTotalScore * 1.15) return true;

            // 10% better top version
            var bestCurrent = current.Max(v => v.RankScore);
            var bestProposed = proposed.Max(v => v.SelectedScore);
            return bestProposed > bestCurrent * 1.10;
        }

        // ── Bucket matching ────────────────────────────────────────────────────

        private static string EditionKey(ParsedStream stream) =>
            string.IsNullOrWhiteSpace(stream.Edition)
            || stream.Edition.Contains("Theatrical", StringComparison.OrdinalIgnoreCase)
                ? "standard"
                : stream.Edition.Trim();

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
            // and "DD/DTS (Compressed)" (lossy surround is still surround)
            if (bucketAudio.Contains("5.1") || bucketAudio.Contains("7.1") || bucketAudio.Contains("Surround"))
            {
                if (streamAudio.Equals("Lossless/Premium", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (streamAudio.Contains("5.1") || streamAudio.Contains("Surround"))
                    return true;
                if (streamAudio.Contains("DD/DTS") || streamAudio.Contains("Compressed"))
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

            // Append best visual tag to resolution (e.g. "4K DV", "1080p HDR10+")
            var resBase = stream.Resolution == "Unknown" ? "" : stream.Resolution;
            var visualTag = BestVisualTag(stream.VisualTags);
            var resPart = !string.IsNullOrEmpty(visualTag) && !string.IsNullOrEmpty(resBase)
                ? $"{resBase} {visualTag}"
                : !string.IsNullOrEmpty(visualTag) ? visualTag : resBase;

            var sourcePart = stream.SourceTag == "Unknown" ? "" : stream.SourceTag;

            var parts = new[] { resPart, sourcePart, audioPart, sizePart }
                .Where(p => !string.IsNullOrEmpty(p));
            var label = string.Join(" - ", parts);

            // Edition prefix (e.g. "Extended - 4K DV - TrueHD - 45.0GiB")
            if (!string.IsNullOrEmpty(stream.Edition) &&
                !stream.Edition.Contains("Theatrical", StringComparison.OrdinalIgnoreCase))
                label = $"{stream.Edition} - {label}";

            // Prefix for library / SeaDex streams
            if (stream.IsLibrary) label = $"[Library] {label}";
            else if (stream.IsSeadexBest) label = $"[SeaDex] {label}";

            return new SelectedVersion
            {
                Stream = stream,
                VersionLabel = label,
                SelectedScore = stream.RankScore,
                MatchedBucket = bucketLabel,
            };
        }

        private static string BestVisualTag(List<string>? tags)
        {
            if (tags == null || tags.Count == 0) return "";
            foreach (var tag in tags)
                if (tag.Contains("DV", StringComparison.OrdinalIgnoreCase) ||
                    tag.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase)) return "DV";
            foreach (var tag in tags)
                if (tag.Contains("HDR10+", StringComparison.OrdinalIgnoreCase)) return "HDR10+";
            foreach (var tag in tags)
                if (tag.Contains("HDR10", StringComparison.OrdinalIgnoreCase)) return "HDR10";
            foreach (var tag in tags)
                if (tag.Contains("HDR", StringComparison.OrdinalIgnoreCase)) return "HDR";
            return "";
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
