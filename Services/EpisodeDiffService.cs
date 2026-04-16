using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Identifies a single episode by season + episode number.
    /// </summary>
    public record EpisodeKey(int Season, int Episode);

    /// <summary>
    /// Result of comparing two episode sets.
    /// </summary>
    public record EpisodeDiff(
        List<EpisodeKey> AddedEpisodes,
        List<EpisodeKey> RemovedEpisodes,
        int UnchangedCount);

    /// <summary>
    /// Compares previous vs current Videos[] to detect added/removed episodes.
    /// Used by RefreshTask for catalog-first episode sync.
    /// </summary>
    public static class EpisodeDiffService
    {
        /// <summary>
        /// Diffs two episode sets. Null previous = all episodes are "added".
        /// Null current = treat as empty (nothing removed from a null baseline).
        /// </summary>
        public static EpisodeDiff DiffEpisodes(string? previousVideosJson, List<StremioVideo>? currentVideos)
        {
            var previous = ParseVideoKeys(previousVideosJson);
            var current = ExtractKeys(currentVideos);

            var added = current.Except(previous).ToList();
            var removed = previous.Except(current).ToList();
            var unchanged = previous.Intersect(current).Count();

            return new EpisodeDiff(added, removed, unchanged);
        }

        /// <summary>
        /// Extracts episode keys from a StremioVideo list.
        /// Filters out entries without both Season and Episode/Number.
        /// </summary>
        private static HashSet<EpisodeKey> ExtractKeys(List<StremioVideo>? videos)
        {
            if (videos == null) return new HashSet<EpisodeKey>();

            var keys = new HashSet<EpisodeKey>();
            foreach (var v in videos)
            {
                if (v.Season.HasValue && (v.Episode.HasValue || v.Number.HasValue))
                    keys.Add(new EpisodeKey(v.Season!.Value, v.Episode ?? v.Number!.Value));
            }
            return keys;
        }

        /// <summary>
        /// Parses episode keys from stored videos_json.
        /// Format: serialized List of {Season, Episode} objects.
        /// Sprint 370: Made public for StrmWriterService.WriteEpisodesFromVideosJsonAsync.
        /// </summary>
        public static HashSet<EpisodeKey> ParseVideoKeys(string? json)
        {
            if (string.IsNullOrEmpty(json)) return new HashSet<EpisodeKey>();

            try
            {
                var items = JsonSerializer.Deserialize<List<StoredVideo>>(json);
                if (items == null) return new HashSet<EpisodeKey>();

                var keys = new HashSet<EpisodeKey>();
                foreach (var v in items)
                {
                    if (v.Season.HasValue && v.Episode.HasValue)
                        keys.Add(new EpisodeKey(v.Season.Value, v.Episode.Value));
                }
                return keys;
            }
            catch
            {
                return new HashSet<EpisodeKey>();
            }
        }

        /// <summary>
        /// Serializes a list of StremioVideo to the compact storage format.
        /// Only stores Season + Episode + Title for diff purposes.
        /// </summary>
        public static string SerializeForStorage(List<StremioVideo>? videos)
        {
            if (videos == null || videos.Count == 0) return "[]";

            var stored = videos
                .Where(v => v.Season.HasValue && (v.Episode.HasValue || v.Number.HasValue))
                .Select(v => new StoredVideo
                {
                    Season = v.Season,
                    Episode = v.Episode ?? v.Number,
                    Title = v.Name
                })
                .ToList();

            return JsonSerializer.Serialize(stored);
        }

        private class StoredVideo
        {
            public int? Season { get; set; }
            public int? Episode { get; set; }
            public string? Title { get; set; }
        }
    }
}
