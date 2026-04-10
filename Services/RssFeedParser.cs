using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Parse-only RSS feed parser for public Trakt and MDBList feeds.
    /// Takes raw RSS XML in, returns structured items out.
    /// No network calls — HTTP is the caller's responsibility.
    /// </summary>
    public static class RssFeedParser
    {
        /// <summary>Hard cap: drop items past this index per feed.</summary>
        private const int MaxItemsPerFeed = 1000;

        private static readonly Regex ImdbRegex =
            new(@"tt\d{7,8}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Represents one normalised item extracted from an RSS feed.
        /// </summary>
        public sealed record RssItem(
            string Title,
            int? Year,
            string? ImdbId,
            string? Link,
            string? Summary);

        /// <summary>
        /// Parses a raw RSS XML string and returns the feed title plus items.
        /// Items without a resolvable IMDb ID are excluded and their count
        /// is reflected in the returned <paramref name="skippedNoImdb"/> out param.
        /// Items past <see cref="MaxItemsPerFeed"/> are silently dropped.
        /// </summary>
        /// <param name="xml">Raw RSS feed XML content.</param>
        /// <param name="logger">Logger for warnings (nullable).</param>
        /// <param name="feedTitle">The &lt;title&gt; of the RSS channel, or null if absent.</param>
        /// <param name="skippedNoImdb">Count of items that had no extractable IMDb ID.</param>
        /// <returns>Normalised items, capped at <see cref="MaxItemsPerFeed"/>.</returns>
        public static IReadOnlyList<RssItem> Parse(
            string xml,
            ILogger? logger,
            out string? feedTitle,
            out int skippedNoImdb)
        {
            feedTitle = null;
            skippedNoImdb = 0;

            var results = new List<RssItem>();
            var rawCount = 0;

            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);

                // Feed title — try Atom <title> and RSS <channel><title>
                var titleNode = doc.SelectSingleNode("//channel/title")
                             ?? doc.SelectSingleNode("//*[local-name()='feed']/*[local-name()='title']");
                if (titleNode != null)
                    feedTitle = titleNode.InnerText?.Trim();

                // Collect items from RSS <item> and Atom <entry>
                var itemNodes = doc.SelectNodes("//channel/item")
                             ?? doc.SelectNodes("//*[local-name()='entry']");

                if (itemNodes == null)
                    return results;

                foreach (XmlNode item in itemNodes)
                {
                    rawCount++;

                    if (results.Count >= MaxItemsPerFeed)
                    {
                        logger?.LogWarning(
                            "[RssFeedParser] Feed exceeds {Cap}-item cap — dropping remaining {Dropped} items",
                            MaxItemsPerFeed, itemNodes.Count - MaxItemsPerFeed);
                        break;
                    }

                    var title   = item.SelectSingleNode("title")?.InnerText?.Trim() ?? string.Empty;
                    var link    = item.SelectSingleNode("link")?.InnerText?.Trim()
                               ?? item.SelectSingleNode("*[local-name()='link']")?.Attributes?["href"]?.Value;
                    var guid    = item.SelectSingleNode("guid")?.InnerText?.Trim();
                    var summary = item.SelectSingleNode("description")?.InnerText?.Trim()
                               ?? item.SelectSingleNode("*[local-name()='summary']")?.InnerText?.Trim();

                    // Extract IMDb ID from link or guid
                    string? imdbId = ExtractImdbId(link) ?? ExtractImdbId(guid);

                    if (imdbId == null)
                    {
                        skippedNoImdb++;
                        continue;
                    }

                    // Attempt to extract year from title  e.g. "The Batman (2022)"
                    int? year = ExtractYear(title);

                    results.Add(new RssItem(title, year, imdbId, link, summary));
                }
            }
            catch (XmlException ex)
            {
                logger?.LogWarning(ex, "[RssFeedParser] XML parse error — returning {Count} items collected so far", results.Count);
            }

            return results;
        }

        /// <summary>
        /// Auto-detects the RSS service from the host name.
        /// Returns "trakt" or "mdblist".
        /// Throws <see cref="ArgumentException"/> for unsupported hosts.
        /// </summary>
        public static string DetectService(string rssUrl)
        {
            if (string.IsNullOrWhiteSpace(rssUrl))
                throw new ArgumentException("RSS URL must not be empty", nameof(rssUrl));

            if (!Uri.TryCreate(rssUrl, UriKind.Absolute, out var uri))
                throw new ArgumentException($"Invalid URL: {rssUrl}", nameof(rssUrl));

            var host = uri.Host.ToLowerInvariant();

            if (host.EndsWith("trakt.tv", StringComparison.Ordinal))
                return "trakt";

            if (host.EndsWith("mdblist.com", StringComparison.Ordinal))
                return "mdblist";

            throw new ArgumentException(
                $"Unsupported RSS host '{host}'. Only trakt.tv and mdblist.com are supported.",
                nameof(rssUrl));
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private static string? ExtractImdbId(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = ImdbRegex.Match(text);
            return m.Success ? m.Value.ToLowerInvariant() : null;
        }

        private static readonly Regex YearRegex =
            new(@"\((\d{4})\)\s*$", RegexOptions.Compiled);

        private static int? ExtractYear(string title)
        {
            if (string.IsNullOrEmpty(title)) return null;
            var m = YearRegex.Match(title);
            return m.Success && int.TryParse(m.Groups[1].Value, out var y) ? y : (int?)null;
        }
    }
}
