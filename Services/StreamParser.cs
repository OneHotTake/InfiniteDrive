using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using InfiniteDrive.Models;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Converts raw AioStreamsStream objects into ParsedStream DTOs with
    /// normalised resolution, audio group, source tag, and composite rank score.
    /// Uses CandidateNormalizer for three-tier parsing and StreamHelpers for scoring.
    /// </summary>
    public static class StreamParser
    {
        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Parses a list of raw AIOStreams streams into ranked ParsedStream objects.
        /// Skips streams without a playable URL or with live/torrent type.
        /// </summary>
        public static List<ParsedStream> ParseAll(IEnumerable<AioStreamsStream> rawStreams)
        {
            var results = new List<ParsedStream>();

            foreach (var raw in rawStreams)
            {
                if (string.IsNullOrEmpty(raw.Url)) continue;

                var streamType = raw.StreamType ?? "debrid";
                var policy = StreamTypePolicy.Get(streamType);
                if (policy.IsLive || string.Equals(streamType, "torrent", StringComparison.OrdinalIgnoreCase))
                    continue;

                var parsed = ParseSingle(raw);
                if (parsed != null)
                    results.Add(parsed);
            }

            // Sort by RankScore descending (best first)
            return results.OrderByDescending(p => p.RankScore).ToList();
        }

        /// <summary>
        /// Parses a single raw stream into a ParsedStream, or null if unusable.
        /// </summary>
        public static ParsedStream? ParseSingle(AioStreamsStream raw)
        {
            if (string.IsNullOrEmpty(raw.Url)) return null;

            // Use CandidateNormalizer for structured metadata
            var tech = CandidateNormalizer.ParseTechnicalMetadata(raw);

            var resolution = NormaliseResolution(tech.Resolution);
            var audioGroup = ClassifyAudioGroup(tech.AudioCodec, tech.AudioChannels, raw);
            var audioPretty = BuildAudioPretty(tech.AudioCodec, tech.AudioChannels, raw);
            var sourceTag = NormaliseSourceTag(tech.SourceType);
            var sizeGiB = ExtractSizeGiB(raw);
            var qualityTier = StreamHelpers.ParseQualityTier(
                !string.IsNullOrEmpty(raw.ParsedFile?.Resolution)
                    ? raw.ParsedFile.Resolution
                    : raw.BehaviorHints?.Filename);

            // Composite score: quality tier + audio bonus + source bonus + cache bonus
            int score = StreamHelpers.TierScore(qualityTier);
            score += AudioGroupScore(audioGroup);
            score += SourceTagScore(sourceTag);
            if (raw.Service?.Cached == true) score += 5;

            // StreamKey for dedup
            var streamKey = !string.IsNullOrEmpty(raw.InfoHash) && raw.FileIdx.HasValue
                ? $"{raw.InfoHash}:{raw.FileIdx}"
                : raw.Url;

            return new ParsedStream
            {
                Resolution = resolution,
                AudioGroup = audioGroup,
                AudioPretty = audioPretty,
                SourceTag = sourceTag,
                SizeGiB = sizeGiB,
                Url = raw.Url!,
                RankScore = score,
                RawStreamJson = JsonSerializer.Serialize(raw),
                StreamKey = streamKey,
            };
        }

        // ── Resolution normalisation ──────────────────────────────────────────

        private static string NormaliseResolution(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
            var r = raw.Trim();
            if (r.Contains("2160") || r.Contains("4K", StringComparison.OrdinalIgnoreCase)) return "4K";
            if (r.Contains("1080")) return "1080p";
            if (r.Contains("720")) return "720p";
            if (r.Contains("480") || r.Contains("SD", StringComparison.OrdinalIgnoreCase)) return "SD";
            return "Unknown";
        }

        // ── Audio classification ──────────────────────────────────────────────

        private static string ClassifyAudioGroup(string? codec, string? channels, AioStreamsStream raw)
        {
            // Use parsedFile audio tags + channels for best accuracy
            var audioTags = raw.ParsedFile?.AudioTags;
            var ch = raw.ParsedFile?.Channels ?? channels;

            // Check for lossless/premium codecs
            if (HasAny(audioTags, "flac", "truehd", "atmos", "dts-hd", "dtshd", "ma"))
                return "Lossless/Premium";

            // Check channels for surround
            if (!string.IsNullOrEmpty(ch))
            {
                if (ch.Contains("7.1") || ch.Contains("5.1"))
                    return HasAny(audioTags, "dd", "dts", "dd+", "ddp")
                        ? "DD/DTS (Compressed)"
                        : "5.1/7.1 (Surround)";
                if (ch.Contains("2.0") || ch.Contains("stereo", StringComparison.OrdinalIgnoreCase))
                    return "Stereo/2.0";
            }

            // Fallback: parse description/filename for audio hints
            var desc = (raw.Description ?? raw.Title ?? raw.Name ?? "").ToLowerInvariant();
            if (desc.Contains("atmos") || desc.Contains("truehd") || desc.Contains("dts-hd ma") || desc.Contains("flac"))
                return "Lossless/Premium";
            if (desc.Contains("5.1") || desc.Contains("7.1"))
                return desc.Contains("dd+") || desc.Contains("ddp") || desc.Contains("dts")
                    ? "DD/DTS (Compressed)"
                    : "5.1/7.1 (Surround)";

            return "Any";
        }

        private static string BuildAudioPretty(string? codec, string? channels, AioStreamsStream raw)
        {
            var parts = new List<string>();
            var audioTags = raw.ParsedFile?.AudioTags ?? new List<string>();
            var ch = raw.ParsedFile?.Channels ?? channels ?? "";

            // Channel config
            if (ch.Contains("7.1")) parts.Add("7.1");
            else if (ch.Contains("5.1")) parts.Add("5.1");

            // Codec from tags
            foreach (var tag in audioTags)
            {
                var t = tag.Trim();
                if (t.Equals("atmos", StringComparison.OrdinalIgnoreCase)) { parts.Add("Atmos"); continue; }
                if (t.Equals("truehd", StringComparison.OrdinalIgnoreCase)) { parts.Add("TrueHD"); continue; }
                if (t.Contains("dts-hd", StringComparison.OrdinalIgnoreCase) &&
                    t.Contains("ma", StringComparison.OrdinalIgnoreCase)) { parts.Add("DTS-HD MA"); continue; }
                if (t.Contains("dts-hd", StringComparison.OrdinalIgnoreCase)) { parts.Add("DTS-HD"); continue; }
                if (t.Contains("dts", StringComparison.OrdinalIgnoreCase)) { parts.Add("DTS"); continue; }
                if (t.Contains("dd+", StringComparison.OrdinalIgnoreCase) ||
                    t.Contains("ddp", StringComparison.OrdinalIgnoreCase)) { parts.Add("DD+"); continue; }
                if (t.Contains("dd", StringComparison.OrdinalIgnoreCase)) { parts.Add("DD"); continue; }
                if (t.Contains("flac", StringComparison.OrdinalIgnoreCase)) { parts.Add("FLAC"); continue; }
                if (t.Contains("aac", StringComparison.OrdinalIgnoreCase)) { parts.Add("AAC"); continue; }
                if (t.Contains("opus", StringComparison.OrdinalIgnoreCase)) { parts.Add("Opus"); continue; }
            }

            if (parts.Count == 0)
            {
                // Fallback: try description
                var desc = (raw.Description ?? "").ToLowerInvariant();
                if (desc.Contains("atmos")) parts.Add("Atmos");
                if (desc.Contains("truehd")) parts.Add("TrueHD");
                else if (desc.Contains("dts-hd ma")) parts.Add("DTS-HD MA");
                else if (desc.Contains("dts-hd")) parts.Add("DTS-HD");
                else if (desc.Contains("dts")) parts.Add("DTS");
                if (desc.Contains("dd+") || desc.Contains("ddp")) parts.Add("DD+");
                else if (desc.Contains("dd ") || desc.Contains("dd+")) parts.Add("DD");
            }

            return parts.Count > 0 ? string.Join(" ", parts) : "Unknown Audio";
        }

        // ── Source tag normalisation ──────────────────────────────────────────

        private static string NormaliseSourceTag(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
            var s = raw.Trim().ToLowerInvariant();

            if (s.Contains("remux")) return "BluRay Remux";
            if (s.Contains("bluray") || s.Contains("blu-ray")) return "BluRay";
            if (s.Contains("web-dl") || s.Contains("webdl")) return "WEB-DL";
            if (s.Contains("webrip")) return "WEBRip";
            if (s.Contains("hdrip")) return "HDRip";
            if (s.Contains("dvdrip") || s.Contains("dvd")) return "DVDRip";
            if (s.Contains("hdtv")) return "HDTV";
            if (s.Contains("cam") || s.Contains("ts")) return "CAM/TS";
            return char.ToUpper(s[0]) + s[1..];
        }

        // ── Size extraction ───────────────────────────────────────────────────

        private static double ExtractSizeGiB(AioStreamsStream raw)
        {
            // Prefer behaviorHints.videoSize (bytes)
            var bytes = raw.BehaviorHints?.VideoSize ?? raw.Size;
            if (bytes.HasValue && bytes.Value > 0)
                return bytes.Value / (1024.0 * 1024.0 * 1024.0);

            // Fallback: parse from description (e.g. "12.5 GB")
            var desc = raw.Description ?? "";
            var gbMatch = System.Text.RegularExpressions.Regex.Match(
                desc, @"(\d+[\.,]?\d*)\s*GB", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (gbMatch.Success && double.TryParse(gbMatch.Groups[1].Value.Replace(',', '.'), out var gb))
                return gb;

            return 0;
        }

        // ── Scoring helpers ───────────────────────────────────────────────────

        private static int AudioGroupScore(string group) => group switch
        {
            "Lossless/Premium" => 20,
            "5.1/7.1 (Surround)" => 15,
            "DD/DTS (Compressed)" => 10,
            "Stereo/2.0" => 5,
            _ => 0,
        };

        private static int SourceTagScore(string tag) => tag switch
        {
            "BluRay Remux" => 15,
            "BluRay" => 12,
            "WEB-DL" => 10,
            "WEBRip" => 8,
            "HDRip" => 5,
            _ => 0,
        };

        private static bool HasAny(List<string>? tags, params string[] needles)
        {
            if (tags == null || tags.Count == 0) return false;
            foreach (var tag in tags)
                foreach (var needle in needles)
                    if (tag.Contains(needle, StringComparison.OrdinalIgnoreCase))
                        return true;
            return false;
        }
    }
}
