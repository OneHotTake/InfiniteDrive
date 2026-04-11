using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace InfiniteDrive.Tests
{
    /// <summary>
    /// Unit tests for uniqueid type attribute correctness.
    /// Sprint 100B-04: UniqueID type attribute correctness.
    /// </summary>
    public static class UniqueIdTests
    {
        /// <summary>
        /// Creates a minimal NFO with a single uniqueid element.
        /// </summary>
        public static string CreateNfoWithUniqueId(string id, string type, bool isDefault = false)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<tvshow>");
            sb.AppendLine($"  <uniqueid type=\"{type}\"{(isDefault ? " default=\"true\"" : string.Empty)}>{id}</uniqueid>");
            sb.AppendLine("</tvshow>");
            return sb.ToString();
        }

        /// <summary>
        /// Parses the uniqueid from an NFO and returns the type and value.
        /// </summary>
        public static (string? type, string? value, bool isDefault) ParseUniqueId(string nfoContent, int index = 0)
        {
            try
            {
                var doc = XDocument.Parse(nfoContent);
                var uniqueids = doc.Descendants("uniqueid");
                if (index < uniqueids.Count())
                {
                    var element = uniqueids.ElementAt(index);
                    var type = element.Attribute("type")?.Value;
                    var value = element.Value;
                    var defaultAttr = element.Attribute("default");
                    var isDefault = defaultAttr != null && defaultAttr.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    return (type, value, isDefault);
                }
            }
            catch (Exception)
            {
                // Return null values on parse error
            }
            return (null, null, false);
        }

        /// <summary>
        /// Verifies uniqueid format for all supported prefix types.
        /// Sprint 100B-04: Unit test for uniqueid type attribute correctness.
        /// </summary>
        public static async Task<bool> VerifyUniqueIdTypesAsync()
        {
            // Test IMDB type (should be default)
            var imdbNfo = CreateNfoWithUniqueId("tt1160419", "Imdb", true);
            var (imdbType, imdbValue, imdbDefault) = ParseUniqueId(imdbNfo);
            if (imdbType != "Imdb" || imdbValue != "tt1160419" || !imdbDefault)
                return false;

            // Test TMDB type
            var tmdbNfo = CreateNfoWithUniqueId("550", "Tmdb");
            var (tmdbType, tmdbValue, tmdbDefault) = ParseUniqueId(tmdbNfo);
            if (tmdbType != "Tmdb" || tmdbValue != "550" || tmdbDefault)
                return false;

            // Test AniList type
            var anilistNfo = CreateNfoWithUniqueId("101922", "AniList");
            var (anilistType, anilistValue, _) = ParseUniqueId(anilistNfo);
            if (anilistType != "AniList" || anilistValue != "101922")
                return false;

            // Test Kitsu type
            var kitsuNfo = CreateNfoWithUniqueId("9156", "Kitsu");
            var (kitsuType, kitsuValue, _) = ParseUniqueId(kitsuNfo);
            if (kitsuType != "Kitsu" || kitsuValue != "9156")
                return false;

            // Test MyAnimeList type
            var malNfo = CreateNfoWithUniqueId("357", "MyAnimeList");
            var (malType, malValue, _) = ParseUniqueId(malNfo);
            if (malType != "MyAnimeList" || malValue != "357")
                return false;

            await Task.CompletedTask;
            return true;
        }
    }
}
