using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Fetches source manifests from AIOStreams.
    /// </summary>
    public class ManifestFetcher
    {
        private readonly AioStreamsClient _client;
        private readonly DatabaseManager _db;
        private readonly ILogger<ManifestFetcher> _logger;

        public ManifestFetcher(AioStreamsClient client, DatabaseManager db, ILogger<ManifestFetcher> logger)
        {
            _client = client;
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Fetches all catalog entries from a single source URL.
        /// </summary>
        public async Task<List<ManifestEntry>> FetchManifestAsync(string sourceUrl, CancellationToken ct = default)
        {
            try
            {
                // Parse source URL to extract base URL
                var baseUrl = sourceUrl.TrimEnd('/');
                var catalogId = GetCatalogIdFromUrl(sourceUrl);

                // Fetch catalog entries (assuming "movie" type for now)
                var catalogResponse = await _client.GetCatalogAsync(
                    "movie",
                    catalogId ?? "aiostreams",
                    null,
                    null,
                    0,
                    ct);

                var entries = new List<ManifestEntry>();

                if (catalogResponse?.Metas != null)
                {
                    foreach (var meta in catalogResponse.Metas)
                    {
                        entries.Add(new ManifestEntry
                        {
                            Id = meta.Id ?? "",
                            Name = meta.Name ?? "",
                            Type = meta.Type ?? "movie",
                            Year = ParseYear(meta.ReleaseInfo),
                            SourceId = catalogId
                        });
                    }
                }

                _logger.LogInformation("[ManifestFetcher] Fetched manifest from {Url}: {Count} entries",
                    sourceUrl, entries.Count);
                return entries;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ManifestFetcher] Failed to fetch manifest from {Url}", sourceUrl);
                return new List<ManifestEntry>();
            }
        }

        /// <summary>
        /// Fetches manifests from all enabled sources.
        /// </summary>
        public async Task<List<ManifestEntry>> FetchAllManifestsAsync(CancellationToken ct = default)
        {
            var sources = await _db.GetEnabledSourcesAsync(ct);
            var allEntries = new List<ManifestEntry>();

            foreach (var source in sources)
            {
                try
                {
                    if (!string.IsNullOrEmpty(source.Url))
                    {
                        _logger.LogWarning("[ManifestFetcher] Source {Name} has no URL, skipping", source.Name);
                        continue;
                    }

                    var entries = await FetchManifestAsync(source.Url, ct);
                    allEntries.AddRange(entries);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ManifestFetcher] Failed to fetch manifest for source {Name}", source.Name);
                    // Continue with other sources
                }
            }

            return allEntries;
        }

        /// <summary>
        /// Extracts catalog ID from a source URL.
        /// </summary>
        private string? GetCatalogIdFromUrl(string url)
        {
            // Parse catalog ID from URL like "https://example.com/manifest.json" or "https://example.com/catalog/movie/mycatalog.json"
            var uri = new Uri(url);
            var pathParts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            // If URL ends with a non-manifest.json segment, use it as catalog ID
            foreach (var part in pathParts)
            {
                if (!part.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                    !part.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    return part;
                }
            }

            return null;
        }

        /// <summary>
        /// Parses year from release info string.
        /// </summary>
        private int? ParseYear(string? releaseInfo)
        {
            if (string.IsNullOrEmpty(releaseInfo))
                return null;

            // Extract year from strings like "2022", "2022–", "2022-2023"
            var yearStr = releaseInfo.Split('–', '-')[0].Trim();
            if (int.TryParse(yearStr, out var year))
                return year;

            return null;
        }
    }
}
