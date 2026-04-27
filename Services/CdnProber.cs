using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Runs ffprobe on a CDN URL and returns real MediaStream objects
    /// with accurate language, codec, and channel information.
    /// Results are cached in stream_candidates.probe_json.
    /// </summary>
    public static class CdnProber
    {
        private const int TimeoutMs = 5000;

        public static async Task<List<MediaStream>?> ProbeAsync(
            string cdnUrl, ILogger logger, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments =
                        "-v error -print_format json -show_streams " +
                        $"-analyzeduration {TimeoutMs * 1000} -probesize {TimeoutMs * 1000} " +
                        $"-rw_timeout {TimeoutMs * 1000} " +
                        "-user_agent \"InfiniteDrive/1.0\" " +
                        $"\"{cdnUrl}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = new Process { StartInfo = psi };
                proc.Start();

                var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);

                if (!proc.WaitForExit(TimeoutMs + 2000))
                {
                    try { proc.Kill(); } catch { }
                    logger.LogDebug("[CdnProber] Timed out probing {Url}", TruncateUrl(cdnUrl));
                    return null;
                }

                if (proc.ExitCode != 0)
                {
                    logger.LogDebug("[CdnProber] Exit={Code} for {Url}: {Err}",
                        proc.ExitCode, TruncateUrl(cdnUrl), Truncate(stderr, 200));
                    return null;
                }

                var streams = ParseProbeOutput(stdout);
                if (streams == null || streams.Count == 0)
                {
                    logger.LogDebug("[CdnProber] No streams parsed from {Url}", TruncateUrl(cdnUrl));
                    return null;
                }

                logger.LogInformation("[CdnProber] {Count} streams found for {Url}",
                    streams.Count, TruncateUrl(cdnUrl));

                return streams;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[CdnProber] Exception probing {Url}", TruncateUrl(cdnUrl));
                return null;
            }
        }

        /// <summary>
        /// Parses ffprobe JSON output into MediaStream objects.
        /// ffprobe -show_streams returns { "streams": [ ... ] }.
        /// </summary>
        private static List<MediaStream>? ParseProbeOutput(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("streams", out var streamsArr))
                return null;

            var result = new List<MediaStream>();
            var index = 0;

            foreach (var s in streamsArr.EnumerateArray())
            {
                var codecType = s.TryGetProperty("codec_type", out var ct) ? ct.GetString() : null;
                if (codecType == null) continue;

                var ms = codecType switch
                {
                    "video" => ParseVideoStream(s, ref index),
                    "audio" => ParseAudioStream(s, ref index),
                    "subtitle" => ParseSubtitleStream(s, ref index),
                    _ => null
                };

                if (ms != null)
                    result.Add(ms);
            }

            return result;
        }

        private static MediaStream ParseVideoStream(JsonElement s, ref int index)
        {
            var ms = new MediaStream
            {
                Type = MediaStreamType.Video,
                Index = index++,
                IsDefault = true,
            };

            if (s.TryGetProperty("codec_name", out var codec))
                ms.Codec = codec.GetString();

            if (s.TryGetProperty("width", out var w) && w.TryGetInt32(out var width))
                ms.Width = width;

            if (s.TryGetProperty("height", out var h) && h.TryGetInt32(out var height))
                ms.Height = height;

            if (s.TryGetProperty("bit_rate", out var br) && br.TryGetInt32(out var bitrate))
                ms.BitRate = bitrate / 1000; // bps → kbps

            return ms;
        }

        private static MediaStream ParseAudioStream(JsonElement s, ref int index)
        {
            var lang = GetTag(s, "language");
            var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
            var channels = s.TryGetProperty("channels", out var ch) ? ch.GetInt32() : 2;
            var channelLayout = s.TryGetProperty("channel_layout", out var cl) ? cl.GetString() : null;
            var title = GetTag(s, "title");

            var displayTitle = !string.IsNullOrEmpty(lang) ? lang : "und";
            if (!string.IsNullOrEmpty(channelLayout) && channelLayout != "stereo")
                displayTitle += $" - {channelLayout}";

            var ms = new MediaStream
            {
                Type = MediaStreamType.Audio,
                Index = index++,
                Language = NormalizeLanguage(lang),
                Title = title ?? displayTitle,
                DisplayTitle = displayTitle,
                Codec = codec,
                Channels = channels,
                ChannelLayout = channelLayout,
            };

            return ms;
        }

        private static MediaStream ParseSubtitleStream(JsonElement s, ref int index)
        {
            var lang = GetTag(s, "language");
            var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
            var title = GetTag(s, "title");

            return new MediaStream
            {
                Type = MediaStreamType.Subtitle,
                Index = index++,
                Language = NormalizeLanguage(lang),
                Title = title ?? lang ?? "und",
                DisplayTitle = !string.IsNullOrEmpty(title) ? title : (lang ?? "und"),
                Codec = MapSubtitleCodec(codec),
                IsExternal = false,
                IsDefault = false,
            };
        }

        private static string? GetTag(JsonElement stream, string tagKey)
        {
            if (stream.TryGetProperty("tags", out var tags) &&
                tags.TryGetProperty(tagKey, out var val))
                return val.GetString();

            // ffprobe sometimes puts tags as TAG:language
            if (stream.TryGetProperty($"TAG:{tagKey}", out var directTag))
                return directTag.GetString();

            return null;
        }

        private static string? NormalizeLanguage(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var lower = raw.ToLowerInvariant().Trim();
            // Map common full names to ISO 639-1/2 codes
            return lower switch
            {
                "english" => "eng",
                "japanese" => "jpn",
                "italian" => "ita",
                "french" => "fre",
                "german" => "ger",
                "spanish" => "spa",
                "portuguese" => "por",
                "korean" => "kor",
                "chinese" => "chi",
                "mandarin" => "chi",
                "russian" => "rus",
                "arabic" => "ara",
                "hindi" => "hin",
                "thai" => "tha",
                "turkish" => "tur",
                "polish" => "pol",
                "dutch" => "dut",
                "swedish" => "swe",
                "norwegian" => "nor",
                "danish" => "dan",
                "finnish" => "fin",
                "czech" => "cze",
                "hungarian" => "hun",
                "greek" => "gre",
                "hebrew" => "heb",
                "romanian" => "rum",
                "bulgarian" => "bul",
                "ukrainian" => "ukr",
                "vietnamese" => "vie",
                "indonesian" => "ind",
                "malay" => "may",
                _ => lower.Length <= 3 ? lower : raw
            };
        }

        private static string? MapSubtitleCodec(string? codec)
        {
            if (string.IsNullOrEmpty(codec)) return "srt";
            return codec switch
            {
                "ass" or "ssa" => "ass",
                "subrip" => "srt",
                "mov_text" => "srt",
                "webvtt" => "vtt",
                "dvd_subtitle" or "dvdsub" => "dvdsub",
                "hdmv_pgs_subtitle" or "pgssub" => "pgssub",
                _ => codec
            };
        }

        private static string TruncateUrl(string url) =>
            url.Length > 80 ? url[..80] + "..." : url;

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "...";
    }
}
