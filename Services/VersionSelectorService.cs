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

            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<SelectedVersion>();
            var prioritizeExtended = config?.PrioritizeExtendedEditions == true;

            // ── Phase 1: Bucket matching (ordered priority) ─────────────────────
            foreach (var bucket in desiredBuckets)
            {
                if (results.Count >= hardCap) break;
                var bucketLabel = BucketLabel(bucket);

                if (prioritizeExtended && bucket.Count > 0)
                {
                    int extSlots = (int)Math.Ceiling(bucket.Count / 2.0);
                    int anySlots = bucket.Count - extSlots;

                    // Phase A: fill extended slots
                    var extended = new List<ParsedStream>();
                    foreach (var stream in rankedStreams)
                    {
                        if (extended.Count >= extSlots) break;
                        if (claimed.Contains(stream.Url)) continue;
                        if (MatchesBucket(stream, bucket) && IsExtended(stream, config!))
                        {
                            claimed.Add(stream.Url);
                            extended.Add(stream);
                        }
                    }

                    // Phase B: fill any-edition slots
                    var any = new List<ParsedStream>();
                    foreach (var stream in rankedStreams)
                    {
                        if (any.Count >= anySlots) break;
                        if (claimed.Contains(stream.Url)) continue;
                        if (MatchesBucket(stream, bucket))
                        {
                            claimed.Add(stream.Url);
                            any.Add(stream);
                        }
                    }

                    // Phase C: backfill unused extended slots with remaining best
                    int deficit = extSlots - extended.Count;
                    if (deficit > 0)
                    {
                        foreach (var stream in rankedStreams)
                        {
                            if (deficit <= 0) break;
                            if (claimed.Contains(stream.Url)) continue;
                            if (MatchesBucket(stream, bucket))
                            {
                                claimed.Add(stream.Url);
                                any.Add(stream);
                                deficit--;
                            }
                        }
                    }

                    foreach (var s in extended.Concat(any))
                    {
                        if (results.Count >= hardCap) break;
                        results.Add(MakeVersion(s, bucketLabel));
                    }
                }
                else
                {
                    var matchCount = 0;
                    foreach (var stream in rankedStreams)
                    {
                        if (results.Count >= hardCap) break;
                        if (matchCount >= bucket.Count) break;
                        if (claimed.Contains(stream.Url)) continue;

                        if (MatchesBucket(stream, bucket))
                        {
                            claimed.Add(stream.Url);
                            results.Add(MakeVersion(stream, bucketLabel));
                            matchCount++;
                        }
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
        /// Assigns a secondary CDN URL to each selected version from the unclaimed stream pool.
        /// Prefers streams matching the same resolution + audio group for relevance.
        /// </summary>
        public static void AssignSecondaryUrls(List<SelectedVersion> selected, List<ParsedStream> allStreams)
        {
            if (selected.Count == 0 || allStreams.Count == 0) return;

            // Track which URLs are already claimed as primary or secondary
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in selected)
                claimed.Add(v.Stream.Url);

            // Try to assign same-resolution + same-audio-group first, then any unclaimed
            foreach (var version in selected)
            {
                var best = allStreams.FirstOrDefault(s =>
                    !claimed.Contains(s.Url)
                    && ResolutionMatches(s.Resolution, version.Stream.Resolution)
                    && AudioMatches(s.AudioGroup, version.Stream.AudioGroup));

                if (best == null)
                    best = allStreams.FirstOrDefault(s => !claimed.Contains(s.Url));

                if (best != null)
                {
                    version.SecondaryUrl = best.Url;
                    claimed.Add(best.Url);
                }
            }
        }

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

        private static bool IsExtended(ParsedStream stream, PluginConfiguration config) =>
            !string.IsNullOrEmpty(stream.Edition) &&
            config.ExtendedEditionKeywords.Any(k =>
                stream.Edition.Contains(k, StringComparison.OrdinalIgnoreCase));

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
