using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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

            var resolution = NormaliseResolution(tech.Resolution, raw);
            var audioGroup = ClassifyAudioGroup(tech.AudioCodec, tech.AudioChannels, raw);
            var audioPretty = BuildAudioPretty(tech.AudioCodec, tech.AudioChannels, raw);
            var sourceTag = NormaliseSourceTag(tech.SourceType, raw);
            var sizeGiB = ExtractSizeGiB(raw);
            var visualTags = raw.ParsedFile?.VisualTags;
            var encode = raw.ParsedFile?.Encode;
            // ParsedFile is null on real AIOStreams instances, so recover the edition from the
            // filename (then description/title) — otherwise extended/director's-cut releases are
            // never tagged, collapse into the theatrical version, and never appear in the dropdown.
            var edition = raw.ParsedFile?.Edition;
            if (string.IsNullOrEmpty(edition))
                edition = ExtractEditionFromText(raw.BehaviorHints?.Filename)
                       ?? ExtractEditionFromText(raw.Description)
                       ?? ExtractEditionFromText(raw.Title ?? raw.Name);
            var isLibrary = raw.Library == true || raw.Passthrough == true;
            var isSeadexBest = raw.Seadex?.IsBest == true;
            var isSeadex = raw.Seadex?.IsSeadex == true;

            var qualityTier = StreamHelpers.ParseQualityTier(
                !string.IsNullOrEmpty(raw.ParsedFile?.Resolution)
                    ? raw.ParsedFile.Resolution
                    : raw.BehaviorHints?.Filename);

            // Composite score
            int score = StreamHelpers.TierScore(qualityTier);
            score += AudioGroupScore(audioGroup);
            score += SourceTagScore(sourceTag);
            if (raw.Service?.Cached == true) score += 5;
            if (isLibrary) score += 10;
            if (isSeadexBest) score += 8;
            else if (isSeadex) score += 4;
            score += VisualTagScore(visualTags);
            score += EncodeScore(encode);
            if (raw.Bitrate.HasValue && raw.Bitrate.Value > 0)
                score += Math.Min((int)(raw.Bitrate.Value / 1_000_000), 5);

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
                VisualTags = visualTags,
                Encode = encode,
                Edition = edition,
                IsLibrary = isLibrary,
                IsSeadexBest = isSeadexBest,
                IsSeadex = isSeadex,
            };
        }

        // ── Edition extraction (filename fallback) ────────────────────────────

        // Canonical edition labels, matched in priority order against release text.
        // Word boundaries (\b) work across the dot/underscore separators in filenames.
        private static readonly (Regex Pattern, string Label)[] _editionPatterns =
        {
            (new Regex(@"\bdirector'?s?[._ ]?cut\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Director's Cut"),
            (new Regex(@"\bextended\b",              RegexOptions.IgnoreCase | RegexOptions.Compiled), "Extended"),
            (new Regex(@"\bultimate[._ ]?edition\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Ultimate Edition"),
            (new Regex(@"\bfinal[._ ]?cut\b",        RegexOptions.IgnoreCase | RegexOptions.Compiled), "Final Cut"),
            (new Regex(@"\bredux\b",                 RegexOptions.IgnoreCase | RegexOptions.Compiled), "Redux"),
            (new Regex(@"\bspecial[._ ]?edition\b",  RegexOptions.IgnoreCase | RegexOptions.Compiled), "Special Edition"),
            (new Regex(@"\bunrated\b",               RegexOptions.IgnoreCase | RegexOptions.Compiled), "Unrated"),
            (new Regex(@"\bremastered\b",            RegexOptions.IgnoreCase | RegexOptions.Compiled), "Remastered"),
            (new Regex(@"\bimax\b",                  RegexOptions.IgnoreCase | RegexOptions.Compiled), "IMAX"),
            (new Regex(@"\btheatrical\b",            RegexOptions.IgnoreCase | RegexOptions.Compiled), "Theatrical"),
        };

        private static string? ExtractEditionFromText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            foreach (var (pattern, label) in _editionPatterns)
                if (pattern.IsMatch(text)) return label;
            return null;
        }

        // ── Resolution normalisation ──────────────────────────────────────────

        private static string NormaliseResolution(string? raw, AioStreamsStream stream)
        {
            // Try tech data first
            var result = NormaliseResolutionString(raw);
            if (result != "Unknown") return result;

            // Fallback: bingeGroup (e.g. "provider|720p|BluRay")
            var binge = stream.BehaviorHints?.BingeGroup;
            if (!string.IsNullOrEmpty(binge))
            {
                result = NormaliseResolutionString(binge);
                if (result != "Unknown") return result;
            }

            // Fallback: filename (e.g. "Movie.2007.720p.BrRip.x264.mp4")
            var filename = stream.BehaviorHints?.Filename;
            if (!string.IsNullOrEmpty(filename))
            {
                result = NormaliseResolutionString(filename);
                if (result != "Unknown") return result;
            }

            // Fallback: description (e.g. "▫ 720p")
            var desc = stream.Description;
            if (!string.IsNullOrEmpty(desc))
            {
                result = NormaliseResolutionString(desc);
                if (result != "Unknown") return result;
            }

            return "Unknown";
        }

        private static string NormaliseResolutionString(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
            var r = raw.Trim();
            if (r.Contains("2160") || r.Contains("4K", StringComparison.OrdinalIgnoreCase)) return "4K";
            if (r.Contains("1440")) return "1440p";
            if (r.Contains("1080")) return "1080p";
            if (r.Contains("720")) return "720p";
            if (r.Contains("576") || r.Contains("480") || r.Contains("360")
                || r.Contains("240") || r.Contains("144")
                || r.Contains("SD", StringComparison.OrdinalIgnoreCase)) return "SD";
            return "Unknown";
        }

        // ── Audio classification ──────────────────────────────────────────────

        private static string ClassifyAudioGroup(string? codec, string? channels, AioStreamsStream raw)
        {
            // Use parsedFile audio tags + channels for best accuracy
            var audioTags = raw.ParsedFile?.AudioTags;
            var ch = raw.ParsedFile?.Channels ?? channels;

            // Check for lossless/premium codecs
            // Note: "atmos" alone is NOT lossless — Atmos over DD+ is lossy.
            // Only TrueHD, FLAC, DTS-HD MA, DTS:X codecs indicate actual lossless/premium.
            if (HasAny(audioTags, "flac", "truehd", "dts-hd", "dtshd", "ma", "dts:x"))
                return "Lossless/Premium";

            // Check channels for surround
            if (!string.IsNullOrEmpty(ch))
            {
                if (ch.Contains("7.1") || ch.Contains("6.1") || ch.Contains("5.1"))
                    return HasAny(audioTags, "dd", "dts", "dd+", "ddp", "eac3", "ac3")
                        ? "DD/DTS (Compressed)"
                        : "5.1/7.1 (Surround)";
                if (ch.Contains("2.0") || ch.Contains("stereo", StringComparison.OrdinalIgnoreCase))
                    return "Stereo/2.0";
            }

            // Fallback: parse description/filename for audio hints
            var desc = (raw.Description ?? raw.Title ?? raw.Name ?? "").ToLowerInvariant();
            if (desc.Contains("truehd") || desc.Contains("dts-hd ma") || desc.Contains("flac") || desc.Contains("dts:x"))
                return "Lossless/Premium";
            if (desc.Contains("5.1") || desc.Contains("6.1") || desc.Contains("7.1"))
                return desc.Contains("dd+") || desc.Contains("ddp") || desc.Contains("dts")
                    || desc.Contains("eac3") || desc.Contains("ac3")
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
            else if (ch.Contains("6.1")) parts.Add("6.1");
            else if (ch.Contains("5.1")) parts.Add("5.1");

            // Codec from tags
            foreach (var tag in audioTags)
            {
                var t = tag.Trim();
                if (t.Equals("atmos", StringComparison.OrdinalIgnoreCase)) { parts.Add("Atmos"); continue; }
                if (t.Equals("truehd", StringComparison.OrdinalIgnoreCase)) { parts.Add("TrueHD"); continue; }
                if (t.Contains("dts-hd", StringComparison.OrdinalIgnoreCase) &&
                    t.Contains("ma", StringComparison.OrdinalIgnoreCase)) { parts.Add("DTS-HD MA"); continue; }
                if (t.Contains("dts:x", StringComparison.OrdinalIgnoreCase)) { parts.Add("DTS:X"); continue; }
                if (t.Contains("dts-es", StringComparison.OrdinalIgnoreCase)) { parts.Add("DTS-ES"); continue; }
                if (t.Contains("dts-hd", StringComparison.OrdinalIgnoreCase)) { parts.Add("DTS-HD"); continue; }
                if (t.Contains("dts", StringComparison.OrdinalIgnoreCase)) { parts.Add("DTS"); continue; }
                if (t.Contains("eac3", StringComparison.OrdinalIgnoreCase)) { parts.Add("DD+"); continue; }
                if (t.Contains("dd+", StringComparison.OrdinalIgnoreCase) ||
                    t.Contains("ddp", StringComparison.OrdinalIgnoreCase)) { parts.Add("DD+"); continue; }
                if (t.Contains("ac3", StringComparison.OrdinalIgnoreCase)) { parts.Add("DD"); continue; }
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
                else if (desc.Contains("dts:x")) parts.Add("DTS:X");
                else if (desc.Contains("dts-es")) parts.Add("DTS-ES");
                else if (desc.Contains("dts-hd")) parts.Add("DTS-HD");
                else if (desc.Contains("dts")) parts.Add("DTS");
                if (desc.Contains("dd+") || desc.Contains("ddp")) parts.Add("DD+");
                else if (desc.Contains("dd ") || desc.Contains("dd+")) parts.Add("DD");
            }

            return parts.Count > 0 ? string.Join(" ", parts) : "Unknown Audio";
        }

        // ── Source tag normalisation ──────────────────────────────────────────

        private static string NormaliseSourceTag(string? raw, AioStreamsStream stream)
        {
            // T2-C: Use ParsedFile.Quality directly — structured value from AIOStreams parser
            var pfQuality = stream.ParsedFile?.Quality;
            if (!string.IsNullOrEmpty(pfQuality))
            {
                var q = NormaliseSourceTagString(pfQuality);
                if (q != "Unknown") return q;
            }

            // Try tech data first
            var result = NormaliseSourceTagString(raw);
            if (result != "Unknown") return result;

            // Fallback: bingeGroup (e.g. "provider|720p|BluRay")
            var binge = stream.BehaviorHints?.BingeGroup;
            if (!string.IsNullOrEmpty(binge))
            {
                result = NormaliseSourceTagString(binge);
                if (result != "Unknown") return result;
            }

            // Fallback: filename
            var filename = stream.BehaviorHints?.Filename;
            if (!string.IsNullOrEmpty(filename))
            {
                result = NormaliseSourceTagString(filename);
                if (result != "Unknown") return result;
            }

            // Fallback: description
            var desc = stream.Description;
            if (!string.IsNullOrEmpty(desc))
            {
                result = NormaliseSourceTagString(desc);
                if (result != "Unknown") return result;
            }

            return "Unknown";
        }

        private static string NormaliseSourceTagString(string? raw)
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
            return "Unknown";
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

        private static int VisualTagScore(List<string>? tags)
        {
            if (tags == null || tags.Count == 0) return 0;
            int bonus = 0;
            foreach (var tag in tags)
            {
                if (tag.Contains("DV", StringComparison.OrdinalIgnoreCase) ||
                    tag.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase)) { bonus += 4; continue; }
                if (tag.Contains("HDR10+", StringComparison.OrdinalIgnoreCase)) { bonus += 3; continue; }
                if (tag.Contains("HDR10", StringComparison.OrdinalIgnoreCase)) { bonus += 2; continue; }
                if (tag.Contains("HDR", StringComparison.OrdinalIgnoreCase)) { bonus += 2; continue; }
                if (tag.Contains("10-bit", StringComparison.OrdinalIgnoreCase) ||
                    tag.Contains("10bit", StringComparison.OrdinalIgnoreCase)) { bonus += 1; continue; }
            }
            return bonus;
        }

        private static int EncodeScore(string? encode)
        {
            if (string.IsNullOrEmpty(encode)) return 0;
            if (encode.Contains("AV1", StringComparison.OrdinalIgnoreCase)) return 2;
            if (encode.Contains("x265", StringComparison.OrdinalIgnoreCase) ||
                encode.Contains("HEVC", StringComparison.OrdinalIgnoreCase)) return 1;
            return 0;
        }

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
