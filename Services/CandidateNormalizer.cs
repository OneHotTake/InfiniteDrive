using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using InfiniteDrive.Models;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Three-tier technical metadata parser for raw <see cref="AioStreamsStream"/> payloads.
    /// Parsing priority:
    ///   1. parsedFile fields (most reliable — AIOStreams parser output)
    ///   2. behaviorHints.filename (parse filename for quality markers)
    ///   3. title / description (parse quality string from stream title)
    /// </summary>
    public class CandidateNormalizer
    {
        private readonly ILogger _logger;

        public CandidateNormalizer(ILogger logger)
        {
            _logger = logger;
        }

        // ── Three-tier parsing ─────────────────────────────────────────────────

        public static TechnicalMetadata ParseTechnicalMetadata(AioStreamsStream raw)
        {
            var tech = new TechnicalMetadata();

            // Tier 1: parsedFile (most reliable — structured AIOStreams parser output)
            if (raw.ParsedFile != null)
            {
                tech.Resolution = NormalizeResolution(raw.ParsedFile.Resolution);
                tech.VideoCodec = NormalizeVideoCodec(raw.ParsedFile.Encode);
                tech.SourceType = NormalizeSourceType(raw.ParsedFile.Quality);
                tech.AudioChannels = NormalizeAudioChannels(raw.ParsedFile.Channels);

                if (raw.ParsedFile.VisualTags?.Count > 0)
                    tech.HdrClass = NormalizeHdrClass(string.Join(",", raw.ParsedFile.VisualTags));

                if (raw.ParsedFile.AudioTags?.Count > 0)
                    tech.AudioCodec = NormalizeAudioCodec(string.Join(",", raw.ParsedFile.AudioTags));

                if (!string.IsNullOrEmpty(tech.Resolution) && !string.IsNullOrEmpty(tech.VideoCodec))
                    return tech; // Tier 1 fully satisfied
            }

            // Tier 2: behaviorHints.filename
            var filename = raw.BehaviorHints?.Filename;
            if (!string.IsNullOrEmpty(filename))
            {
                var filenameTech = ParseQualityString(filename);
                tech = MergeTechnical(tech, filenameTech);
                if (!string.IsNullOrEmpty(tech.Resolution) && !string.IsNullOrEmpty(tech.VideoCodec))
                    return tech;
            }

            // Tier 3: title / description
            var title = raw.Title ?? raw.Name;
            if (!string.IsNullOrEmpty(title))
            {
                var titleTech = ParseQualityString(title);
                tech = MergeTechnical(tech, titleTech);
            }

            return tech;
        }

        /// <summary>
        /// Merge two TechnicalMetadata objects. Non-null values from <paramref name="override_"/>
        /// take precedence over <paramref name="base_"/>.
        /// </summary>
        private static TechnicalMetadata MergeTechnical(TechnicalMetadata base_, TechnicalMetadata override_)
        {
            return new TechnicalMetadata
            {
                Resolution = override_.Resolution ?? base_.Resolution,
                VideoCodec = override_.VideoCodec ?? base_.VideoCodec,
                HdrClass = override_.HdrClass ?? base_.HdrClass,
                AudioCodec = override_.AudioCodec ?? base_.AudioCodec,
                AudioChannels = override_.AudioChannels ?? base_.AudioChannels,
                SourceType = override_.SourceType ?? base_.SourceType,
            };
        }

        // ── Quality string parsing (Tiers 2 & 3) ──────────────────────────────

        public static TechnicalMetadata ParseQualityString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return new TechnicalMetadata();

            // Normalize separators: dots, underscores, dashes → spaces
            var normalized = input
                .Replace('.', ' ')
                .Replace('_', ' ')
                .Replace('-', ' ')
                .Replace('[', ' ')
                .Replace(']', ' ')
                .Replace('(', ' ')
                .Replace(')', ' ');

            var tech = new TechnicalMetadata();

            // Resolution
            tech.Resolution = ExtractResolution(normalized);

            // Video codec
            tech.VideoCodec = ExtractVideoCodec(normalized);

            // HDR class
            tech.HdrClass = ExtractHdrClass(normalized);

            // Audio codec
            tech.AudioCodec = ExtractAudioCodec(normalized);

            // Audio channels
            tech.AudioChannels = ExtractAudioChannels(normalized);

            // Source type
            tech.SourceType = ExtractSourceType(normalized);

            return tech;
        }

        // ── Resolution normalization ────────────────────────────────────────────

        internal static string NormalizeResolution(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var lower = raw.Trim().ToLowerInvariant();

            return lower switch
            {
                "4k" or "2160p" or "uhd" => "2160p",
                "1080p" or "fhd" or "fullhd" or "full hd" or "full hd" => "1080p",
                "1080i" => "1080i",
                "720p" or "hd" or "hdready" => "720p",
                "576p" or "sd" => "576p",
                "480p" => "480p",
                _ when lower.Contains("2160") => "2160p",
                _ when lower.Contains("1080") => "1080p",
                _ when lower.Contains("720") => "720p",
                _ when lower.Contains("576") => "576p",
                _ when lower.Contains("480") => "480p",
                _ => null
            };
        }

        private static string ExtractResolution(string normalized)
        {
            // Order matters: check 2160 before 1080 before 720
            if (Regex.IsMatch(normalized, @"\b(2160|4k|uhd)\b", RegexOptions.IgnoreCase))
                return "2160p";
            if (Regex.IsMatch(normalized, @"\b1080[i]?\b"))
                return normalized.Contains("1080i") ? "1080i" : "1080p";
            if (Regex.IsMatch(normalized, @"\b720[p]?\b"))
                return "720p";
            if (Regex.IsMatch(normalized, @"\b576[p]?\b"))
                return "576p";
            if (Regex.IsMatch(normalized, @"\b480[p]?\b"))
                return "480p";
            return null;
        }

        // ── Video codec normalization ───────────────────────────────────────────

        public static string NormalizeVideoCodec(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var lower = raw.Trim().ToLowerInvariant();

            // Direct canonical matches
            if (lower == "h264" || lower == "h.264" || lower == "avc") return "h264";
            if (lower == "hevc" || lower == "h265" || lower == "h.265") return "hevc";
            if (lower == "av1") return "av1";
            if (lower == "vc1" || lower == "vc-1") return "vc1";
            if (lower == "mpeg2") return "mpeg2";

            // x-codec prefixes (release naming: x264, x265)
            if (lower == "x264") return "h264";
            if (lower == "x265") return "hevc";

            return null;
        }

        private static string ExtractVideoCodec(string normalized)
        {
            if (Regex.IsMatch(normalized, @"\b(x265|h265|h\.265|hevc)\b", RegexOptions.IgnoreCase))
                return "hevc";
            if (Regex.IsMatch(normalized, @"\b(av1)\b", RegexOptions.IgnoreCase))
                return "av1";
            if (Regex.IsMatch(normalized, @"\b(x264|h264|h\.264|avc)\b", RegexOptions.IgnoreCase))
                return "h264";
            return null;
        }

        // ── HDR class normalization ─────────────────────────────────────────────

        internal static string NormalizeHdrClass(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var lower = raw.Trim().ToLowerInvariant();

            // Check most specific first
            if (lower.Contains("dolby vision") || lower.Contains(" dv ") || lower == "dv"
                || lower.Contains("dolbyvision"))
                return "dv";

            if (lower.Contains("hdr10+") || lower.Contains("hdr10plus"))
                return "hdr10_plus";

            if (lower.Contains("hdr10"))
                return "hdr10";

            if (lower.Contains("hdr") || lower.Contains("hdr"))
                return "hdr10";

            // No HDR detected — SDR
            return null;
        }

        private static string ExtractHdrClass(string normalized)
        {
            // Order: DV > HDR10+ > HDR10 > generic HDR
            if (Regex.IsMatch(normalized, @"\b(dolby.?vision|\.dv\.|\bdv\b)\b", RegexOptions.IgnoreCase))
                return "dv";
            if (Regex.IsMatch(normalized, @"\bhdr10\+?\b", RegexOptions.IgnoreCase))
                return "hdr10_plus";
            if (Regex.IsMatch(normalized, @"\bhdr10\b", RegexOptions.IgnoreCase))
                return "hdr10";
            if (Regex.IsMatch(normalized, @"\bhdr\b", RegexOptions.IgnoreCase))
                return "hdr10";
            return null;
        }

        // ── Audio codec normalization ───────────────────────────────────────────

        public static string NormalizeAudioCodec(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var lower = raw.Trim().ToLowerInvariant();

            // Order matters: atmos > dd_plus > dd > specific codecs
            if (lower.Contains("atmos")) return "atmos";
            if (lower.Contains("dtsx")) return "dts_x";
            if (lower.Contains("dts-hd") || lower.Contains("dts_hd") || lower.Contains("dtshd")) return "dts_hd";
            if (lower.Contains("dts")) return "dts";
            if (lower.Contains("dd+") || lower.Contains("ddplus")
                || lower.Contains("dolby digital plus") || lower.Contains("dolbydigitalplus"))
                return "dd_plus";
            if (lower.Contains("dd5.1") || lower == "dd" || lower.Contains("dolby digital")
                || lower.Contains("dolbydigital"))
                return "dd";
            if (lower.Contains("flac")) return "flac";
            if (lower.Contains("aac")) return "aac";
            if (lower.Contains("opus")) return "opus";
            if (lower.Contains("mp3")) return "mp3";
            if (lower.Contains("ac3")) return "dd"; // AC3 ≈ Dolby Digital

            return null;
        }

        private static string ExtractAudioCodec(string normalized)
        {
            if (Regex.IsMatch(normalized, @"\batmos\b", RegexOptions.IgnoreCase))
                return "atmos";
            if (Regex.IsMatch(normalized, @"\bdts.?x\b", RegexOptions.IgnoreCase))
                return "dts_x";
            if (Regex.IsMatch(normalized, @"\bdts.?hd\b", RegexOptions.IgnoreCase))
                return "dts_hd";
            if (Regex.IsMatch(normalized, @"\bdts\b", RegexOptions.IgnoreCase))
                return "dts";
            if (Regex.IsMatch(normalized, @"\b(dd\+|ddplus|dolby.?digital.?plus)\b", RegexOptions.IgnoreCase))
                return "dd_plus";
            if (Regex.IsMatch(normalized, @"\b(dolby.?digital|dd5\.1|\bdd\b|ac3)\b", RegexOptions.IgnoreCase))
                return "dd";
            if (Regex.IsMatch(normalized, @"\bflac\b", RegexOptions.IgnoreCase))
                return "flac";
            if (Regex.IsMatch(normalized, @"\baac\b", RegexOptions.IgnoreCase))
                return "aac";
            return null;
        }

        // ── Audio channels normalization ────────────────────────────────────────

        public static string NormalizeAudioChannels(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var lower = raw.Trim().ToLowerInvariant();

            if (lower.Contains("7.1")) return "7.1";
            if (lower.Contains("6.1")) return "6.1";
            if (lower.Contains("5.1")) return "5.1";
            if (lower.Contains("2.0") || lower.Contains("stereo")) return "stereo";
            if (lower.Contains("mono") || lower.Contains("1.0")) return "mono";

            return null;
        }

        private static string ExtractAudioChannels(string normalized)
        {
            if (Regex.IsMatch(normalized, @"\b7\.1\b")) return "7.1";
            if (Regex.IsMatch(normalized, @"\b6\.1\b")) return "6.1";
            if (Regex.IsMatch(normalized, @"\b5\.1\b")) return "5.1";
            if (Regex.IsMatch(normalized, @"\b(2\.0|stereo)\b", RegexOptions.IgnoreCase)) return "stereo";
            if (Regex.IsMatch(normalized, @"\b(mono|1\.0)\b", RegexOptions.IgnoreCase)) return "mono";
            return null;
        }

        // ── Source type normalization ────────────────────────────────────────────

        internal static string NormalizeSourceType(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var lower = raw.Trim().ToLowerInvariant();

            if (lower.Contains("remux")) return "remux";
            if (lower.Contains("bluray") || lower.Contains("blu-ray") || lower.Contains("bd"))
                return "bluray";
            if (lower.Contains("web-dl") || lower.Contains("webdl")) return "web";
            if (lower.Contains("webrip") || lower.Contains("web-rip")) return "web";
            if (lower.Contains("hdtv")) return "hdtv";
            if (lower.Contains("dvdrip") || lower.Contains("dvd")) return "dvd";
            if (lower.Contains("satrip") || lower.Contains("sat")) return "sat";
            if (lower.Contains("cam") || lower.Contains("ts") || lower.Contains("hdcam"))
                return "cam";
            if (lower.Contains("ppv")) return "ppv";

            return null;
        }

        private static string ExtractSourceType(string normalized)
        {
            if (Regex.IsMatch(normalized, @"\bremux\b", RegexOptions.IgnoreCase)) return "remux";
            if (Regex.IsMatch(normalized, @"\b(blu.?ray|bdrip|bd)\b", RegexOptions.IgnoreCase)) return "bluray";
            if (Regex.IsMatch(normalized, @"\b(web.?dl|webdl)\b", RegexOptions.IgnoreCase)) return "web";
            if (Regex.IsMatch(normalized, @"\bwebrip\b", RegexOptions.IgnoreCase)) return "web";
            if (Regex.IsMatch(normalized, @"\bhdtv\b", RegexOptions.IgnoreCase)) return "hdtv";
            if (Regex.IsMatch(normalized, @"\b(dvdrip|dvd)\b", RegexOptions.IgnoreCase)) return "dvd";
            return null;
        }

        /// <summary>
        /// Resolve language list from an AIOStreams stream.
        /// Tier 1: <c>parsedFile.Languages</c> (structured).
        /// Tier 2: extract ISO-639 tokens from <c>behaviorHints.filename</c>.
        /// </summary>
        public static List<string> ResolveLanguages(AioStreamsStream raw)
        {
            // Tier 1: structured parsedFile
            if (raw.ParsedFile?.Languages?.Count > 0)
                return raw.ParsedFile.Languages;

            // Tier 2: filename tokens
            return ExtractLanguagesFromFilename(raw.BehaviorHints?.Filename);
        }

        private static readonly string[] KnownLangTokens = new[]
        {
            "eng","fre","ger","ita","spa","por","rus","jpn","jap","kor","chi","tha","tur",
            "pol","cze","hun","dut","dan","fin","nor","swe","ara","hin","ben","tam",
            "tel","mal","kan","ukr","rum","bul","hrv","slv","srp","bos","lav","lit",
            "est","heb","ind","may","vie","fil"
        };

        private static List<string> ExtractLanguagesFromFilename(string? filename)
        {
            if (string.IsNullOrEmpty(filename))
                return new List<string> { "und" };

            var normalized = filename.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');

            if (Regex.IsMatch(normalized, @"\bMULTi\b", RegexOptions.IgnoreCase))
                return new List<string> { "und" };

            var langs = new List<string>();
            foreach (var token in KnownLangTokens)
            {
                if (Regex.IsMatch(normalized, $@"\b{token}\b", RegexOptions.IgnoreCase))
                    langs.Add(token.ToLowerInvariant());
            }
            return langs.Count > 0 ? langs : new List<string> { "und" };
        }

        // ── Internal record for parsing pipeline ───────────────────────────────

        public class TechnicalMetadata
        {
            public string Resolution;
            public string VideoCodec;
            public string HdrClass;
            public string AudioCodec;
            public string AudioChannels;
            public string SourceType;
        }
    }
}
