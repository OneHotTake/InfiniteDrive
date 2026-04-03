using System;
using EmbyStreams.Services;

namespace EmbyStreams.Tests
{
    /// <summary>
    /// Integration tests for stream URL construction.
    /// Sprint 100B-06: Episode stream ID format test.
    /// </summary>
    public static class StreamUrlTests
    {
        /// <summary>
        /// Tests IMDB-based stream URL construction for movies.
        /// </summary>
        public static string TestMovieStreamUrl(string stremioBase, string imdbId)
        {
            var client = new AioStreamsClient(stremioBase, string.Empty, string.Empty);
            return client.GetMovieStreamUrl(imdbId);
        }

        /// <summary>
        /// Tests IMDB-based stream URL construction for TV episodes.
        /// Format: {stremioBase}/stream/series/{imdbId}:{season}:{episode}.json
        /// </summary>
        public static string TestSeriesStreamUrl(string stremioBase, string imdbId, int season, int episode)
        {
            var client = new AioStreamsClient(stremioBase, string.Empty, string.Empty);
            return client.GetSeriesStreamUrl(imdbId, season, episode);
        }

        /// <summary>
        /// ── FIX-100B-06: Episode stream ID format test ───────────────────
        /// Tests anime stream URL construction with absolute episode numbering.
        /// Format: {stremioBase}/stream/series/{provider}:{seriesId}:{absoluteEpisode}.json
        /// Supported providers: kitsu, anilist
        /// </summary>
        public static (string kitsuUrl, string anilistUrl) TestAnimeStreamUrl(
            string stremioBase,
            string kitsuId,
            string anilistId,
            int absoluteEpisode)
        {
            var client = new AioStreamsClient(stremioBase, string.Empty, string.Empty);
            var kitsuUrl = client.GetAnimeStreamUrl("kitsu", kitsuId, absoluteEpisode);
            var anilistUrl = client.GetAnimeStreamUrl("anilist", anilistId, absoluteEpisode);
            return (kitsuUrl, anilistUrl);
        }

        /// <summary>
        /// ── FIX-100B-05: Absolute episode number calculation test ─────────
        /// Tests absolute episode number calculation.
        /// </summary>
        public static bool TestAbsoluteEpisodeCalculation()
        {
            // Test 1: S01E01 with no previous season data
            var ep1 = AioStreamsClient.CalculateAbsoluteEpisode(1, 1, null);
            if (ep1 != 1) return false;

            // Test 2: S01E05 with no previous season data
            var ep2 = AioStreamsClient.CalculateAbsoluteEpisode(1, 5, null);
            if (ep2 != 5) return false;

            // Test 3: S02E01 with no previous season data (estimate 12 for S01)
            var ep3 = AioStreamsClient.CalculateAbsoluteEpisode(2, 1, null);
            if (ep3 != 13) return false;  // 12 (S01 estimate) + 1 (S02E01)

            // Test 4: S02E05 with no previous season data
            var ep4 = AioStreamsClient.CalculateAbsoluteEpisode(2, 5, null);
            if (ep4 != 17) return false;  // 12 (S01 estimate) + 5 (S02E05)

            // Test 5: With actual season counts (S01: 13, S02: 12)
            var seasonCounts = new[] { 13, 12 };
            var ep5 = AioStreamsClient.CalculateAbsoluteEpisode(2, 5, seasonCounts);
            if (ep5 != 18) return false;  // 13 (S01) + 5 (S02E05)

            // Test 6: S03E10 with actual season counts (S01: 13, S02: 12)
            var ep6 = AioStreamsClient.CalculateAbsoluteEpisode(3, 10, seasonCounts);
            if (ep6 != 35) return false;  // 13 + 12 + 10

            return true;
        }

        /// <summary>
        /// Validates that constructed stream URLs have the expected format.
        /// </summary>
        public static bool ValidateStreamUrlFormat(string url, string expectedPathPrefix)
        {
            return url.Contains(expectedPathPrefix) && url.EndsWith(".json");
        }
    }
}
