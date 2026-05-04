using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;

namespace InfiniteDrive.Services
{
    public partial class AioMediaSourceProvider
    {
        // ── Stream building utilities (static) ────────────────────────────────
        private static bool HasLanguageMatch(MediaSourceInfo source, string lang)
        {
            if (source.MediaStreams == null) return false;
            return source.MediaStreams.Any(ms =>
                ms.Type == MediaStreamType.Audio &&
                !string.IsNullOrEmpty(ms.Language) &&
                (string.Equals(ms.Language, lang, StringComparison.OrdinalIgnoreCase) ||
                 ms.Language.StartsWith(lang, StringComparison.OrdinalIgnoreCase)));
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  MediaStreams builders (unchanged helpers)
        // ═══════════════════════════════════════════════════════════════════════════

        private static List<MediaStream>? DeserializeProbeStreams(string probeJson)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(probeJson);
            var result = new List<MediaStream>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var typeStr = el.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (typeStr == null) continue;

                var ms = new MediaStream
                {
                    Type = Enum.TryParse<MediaStreamType>(typeStr, out var mst) ? mst : MediaStreamType.Video,
                    Codec = el.TryGetProperty("codec", out var c) ? c.GetString() : null,
                    Language = el.TryGetProperty("language", out var l) ? l.GetString() : null,
                    Title = el.TryGetProperty("title", out var ti) ? ti.GetString() : null,
                    DisplayTitle = el.TryGetProperty("title", out var dt) ? dt.GetString() : null,
                    Channels = el.TryGetProperty("channels", out var ch) && ch.TryGetInt32(out var chVal) ? chVal : 0,
                    ChannelLayout = el.TryGetProperty("channelLayout", out var cl) ? cl.GetString() : null,
                    Width = el.TryGetProperty("width", out var w) && w.TryGetInt32(out var wVal) ? wVal : 0,
                    Height = el.TryGetProperty("height", out var h) && h.TryGetInt32(out var hVal) ? hVal : 0,
                    BitRate = el.TryGetProperty("bitRate", out var br) && br.TryGetInt32(out var brVal) ? brVal : 0,
                    IsDefault = el.TryGetProperty("isDefault", out var d) && d.GetBoolean(),
                    Index = result.Count,
                };

                if (ms.Type == MediaStreamType.Subtitle)
                    ms.IsExternal = false;

                result.Add(ms);
            }
            return result.Count > 0 ? result : null;
        }

        private static string SerializeProbeStreams(List<MediaStream> streams)
        {
            return JsonSerializer.Serialize(
                streams.Select(ms => new
                {
                    type = ms.Type.ToString(),
                    codec = ms.Codec,
                    language = ms.Language,
                    title = ms.Title,
                    channels = ms.Channels,
                    channelLayout = ms.ChannelLayout,
                    width = ms.Width,
                    height = ms.Height,
                    bitRate = ms.BitRate,
                    isDefault = ms.IsDefault,
                }));
        }

        private static List<MediaStream> FilterKnownStreams(List<MediaStream> streams)
        {
            var known = streams.Where(s =>
                s.Type == MediaStreamType.Video ||
                s.Type == MediaStreamType.Audio ||
                s.Type == MediaStreamType.Subtitle).ToList();
            return known.Count > 0 ? known : streams;
        }

        private static void AppendSubtitlesFromJson(List<MediaStream> streams, string? subtitlesJson)
        {
            if (string.IsNullOrEmpty(subtitlesJson)) return;
            try
            {
                var subs = JsonSerializer.Deserialize<List<AioStreamsSubtitle>>(subtitlesJson);
                if (subs == null) return;
                foreach (var sub in subs)
                {
                    if (string.IsNullOrEmpty(sub.Url)) continue;
                    streams.Add(new MediaStream
                    {
                        Type               = MediaStreamType.Subtitle,
                        Language           = sub.Lang ?? "und",
                        Title              = sub.Lang ?? "und",
                        DisplayTitle       = sub.Lang ?? "und",
                        IsExternal         = true,
                        Codec              = InferSubtitleCodec(sub.Url),
                        DeliveryUrl        = sub.Url,
                        Path               = sub.Url,
                        DeliveryMethod     = SubtitleDeliveryMethod.External,
                        SupportsExternalStream = true,
                        IsDefault          = false,
                        Index              = streams.Count,
                    });
                }
            }
            catch { /* non-fatal */ }
        }

        private static string InferSubtitleCodec(string? url)
        {
            if (string.IsNullOrEmpty(url)) return "srt";
            if (url.Contains(".vtt", StringComparison.OrdinalIgnoreCase)) return "vtt";
            if (url.Contains(".ass", StringComparison.OrdinalIgnoreCase) || url.Contains(".ssa", StringComparison.OrdinalIgnoreCase)) return "ass";
            return "srt";
        }

        /// <summary>
        /// Builds MediaStreams using CandidateNormalizer's three-tier parser
        /// (parsedFile → filename → description) from the preserved raw AioStreamsStream.
        /// Falls back to filename-only parsing when raw JSON is unavailable.
        /// </summary>
        private static List<MediaStream> BuildRichMediaStreams(StreamCandidate candidate)
        {
            var streams = new List<MediaStream>();

            // Try to deserialize the preserved raw AioStreamsStream
            AioStreamsStream? raw = null;
            if (!string.IsNullOrEmpty(candidate.RawStreamJson))
            {
                try { raw = JsonSerializer.Deserialize<AioStreamsStream>(candidate.RawStreamJson); }
                catch { /* fallback below */ }
            }

            string videoCodec;
            string audioCodec;
            int channels;
            string? audioTitle;
            int width, height;

            if (raw != null)
            {
                // Three-tier parsing via CandidateNormalizer
                var tech = CandidateNormalizer.ParseTechnicalMetadata(raw);
                var langs = CandidateNormalizer.ResolveLanguages(raw);

                videoCodec = tech.VideoCodec ?? "h264";
                audioCodec = MapAudioCodecForEmby(tech.AudioCodec);
                channels = MapChannelCount(tech.AudioChannels);
                audioTitle = BuildAudioTitle(audioCodec, channels);

                // Resolution from tech (more reliable than QualityTier)
                var (w, h) = StreamHelpers.ResolutionToPixels(tech.Resolution ?? candidate.QualityTier);
                width = w;
                height = h;

                var primaryLang = langs.Count > 0 ? langs[0] : "und";

                // ── Video stream ──────────────────────────────────────────────
                streams.Add(new MediaStream
                {
                    Type      = MediaStreamType.Video,
                    Index     = 0,
                    Codec     = videoCodec,
                    Width     = width,
                    Height    = height,
                    Language  = "und",
                    IsDefault = true,
                    BitRate   = candidate.BitrateKbps ?? 0,
                });

                // ── Audio streams (one per detected language) ─────────────────
                var audioLangs = langs.Count > 0 ? langs : new List<string> { "und" };
                for (int i = 0; i < audioLangs.Count; i++)
                {
                    streams.Add(new MediaStream
                    {
                        Type           = MediaStreamType.Audio,
                        Index          = streams.Count,
                        Codec          = audioCodec,
                        Channels       = channels,
                        Language       = audioLangs[i],
                        Title          = audioTitle,
                        DisplayTitle   = audioTitle,
                        IsDefault      = i == 0,
                    });
                }
            }
            else
            {
                // Fallback: filename-only parsing
                var fnUpper = (candidate.FileName ?? "").ToUpperInvariant();
                videoCodec = DetectVideoCodec(fnUpper);
                audioCodec = DetectAudioCodec(fnUpper);
                channels = DetectChannels(fnUpper);
                audioTitle = BuildAudioTitle(audioCodec, channels);
                var (w, h) = StreamHelpers.ResolutionToPixels(candidate.QualityTier);
                width = w;
                height = h;

                var lang = candidate.Languages?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?.Trim() ?? "und";

                streams.Add(new MediaStream
                {
                    Type      = MediaStreamType.Video,
                    Index     = 0,
                    Codec     = videoCodec,
                    Width     = width,
                    Height    = height,
                    Language  = "und",
                    IsDefault = true,
                    BitRate   = candidate.BitrateKbps ?? 0,
                });

                streams.Add(new MediaStream
                {
                    Type           = MediaStreamType.Audio,
                    Index          = 1,
                    Codec          = audioCodec,
                    Channels       = channels,
                    Language       = lang,
                    Title          = audioTitle,
                    DisplayTitle   = audioTitle,
                    IsDefault      = true,
                });
            }

            // ── Subtitles ────────────────────────────────────────────────────
            AppendSubtitlesFromJson(streams, candidate.SubtitlesJson);

            // ── Probe override (if available) ────────────────────────────────
            if (!string.IsNullOrEmpty(candidate.ProbeJson))
            {
                try
                {
                    var probed = DeserializeProbeStreams(candidate.ProbeJson);
                    if (probed != null && probed.Count > 1)
                        return probed;
                }
                catch { /* fallback to parsed */ }
            }

            return streams;
        }

        /// <summary>
        /// Maps CandidateNormalizer audio codec names to Emby-compatible codec strings.
        /// </summary>
        private static string MapAudioCodecForEmby(string? audioCodec) => audioCodec switch
        {
            "atmos"   => "eac3",
            "dts_x"   => "dtshd",
            "dts_hd"  => "dtshd",
            "dts"     => "dts",
            "dd_plus" => "eac3",
            "dd"      => "ac3",
            "flac"    => "flac",
            "aac"     => "aac",
            "opus"    => "opus",
            "mp3"     => "mp3",
            _         => "",
        };

        /// <summary>
        /// Maps CandidateNormalizer channel layout strings to integer channel counts.
        /// </summary>
        private static int MapChannelCount(string? channelLayout) => channelLayout switch
        {
            "7.1"    => 8,
            "6.1"    => 7,
            "5.1"    => 6,
            "stereo" => 2,
            "mono"   => 1,
            _        => 0,
        };

        private static string DetectVideoCodec(string fnUpper)
        {
            if (fnUpper.Contains("X265") || fnUpper.Contains("HEVC") || fnUpper.Contains("H265") || fnUpper.Contains("H.265"))
                return "hevc";
            if (fnUpper.Contains("AV1")) return "av1";
            if (fnUpper.Contains("X264") || fnUpper.Contains("H264") || fnUpper.Contains("H.264") || fnUpper.Contains("AVC"))
                return "h264";
            // Remux is typically AVC (H.264) for 1080p, HEVC for 4K
            if (fnUpper.Contains("REMUX")) return "h264";
            return "h264";
        }

        private static string DetectAudioCodec(string fnUpper)
        {
            if (fnUpper.Contains("ATMOS")) return "eac3";
            if (fnUpper.Contains("TRUEHD")) return "truehd";
            if (fnUpper.Contains("DTS-HD") || fnUpper.Contains("DTSHD") || fnUpper.Contains("DTS-X") || fnUpper.Contains("DTSX"))
                return "dtshd";
            if (fnUpper.Contains("EAC3") || fnUpper.Contains("E-AC3") || fnUpper.Contains("DDP") || fnUpper.Contains("DD+"))
                return "eac3";
            if (fnUpper.Contains("DTS")) return "dts";
            if (fnUpper.Contains("AC3") || fnUpper.Contains("AC-3") || fnUpper.Contains("DD5") || fnUpper.Contains("DOLBY DIGITAL"))
                return "ac3";
            if (fnUpper.Contains("FLAC")) return "flac";
            if (fnUpper.Contains("OPUS")) return "opus";
            if (fnUpper.Contains("AAC")) return "aac";
            return "";
        }

        private static int DetectChannels(string fnUpper)
        {
            if (fnUpper.Contains("7.1") || fnUpper.Contains("8 CH")) return 8;
            if (fnUpper.Contains("5.1") || fnUpper.Contains("6 CH")) return 6;
            if (fnUpper.Contains("2.0") || fnUpper.Contains("STEREO")) return 2;
            return 0; // unknown — let Emby probe the real value
        }

        private static string BuildAudioTitle(string codec, int channels)
        {
            var ch = channels switch
            {
                8 => "7.1",
                6 => "5.1",
                2 => "Stereo",
                _ => ""
            };
            var parts = new[] { codec.ToUpperInvariant(), ch }.Where(p => !string.IsNullOrEmpty(p));
            return parts.Any() ? string.Join(" ", parts) : "";
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  Config helpers
    }
}
