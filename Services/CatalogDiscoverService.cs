using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Logging;
using EmbyStreams.Models;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Helper service that syncs the Discover catalog from AIOStreams.
    /// Fetches available titles and populates the discover_catalog table
    /// with metadata that users can browse and add to their library.
    /// </summary>
    public class CatalogDiscoverService
    {
        private readonly ILogger<CatalogDiscoverService> _logger;
        private readonly DatabaseManager _db;

        public CatalogDiscoverService(ILogManager logManager, DatabaseManager db)
        {
            _logger = new EmbyLoggerAdapter<CatalogDiscoverService>(logManager.GetLogger("EmbyStreams"));
            _db = db;
        }

        /// <summary>
        /// Syncs the Discover catalog from AIOStreams.
        /// This is called by CatalogDiscoverTask on a schedule.
        /// </summary>
        public async Task SyncDiscoverCatalogAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("[Discover] Starting catalog sync...");

                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    _logger.LogWarning("[Discover] Plugin configuration not available");
                    return;
                }

                if (string.IsNullOrWhiteSpace(config.PrimaryManifestUrl)
                    && string.IsNullOrWhiteSpace(config.SecondaryManifestUrl))
                {
                    _logger.LogWarning("[Discover] AIOStreams manifest URL not configured");
                    return;
                }

                // Create a client for the configured AIOStreams
                using (var client = new AioStreamsClient(config, _logger))
                {
                    client.Cooldown = Plugin.Instance?.CooldownGate;
                    // Fetch manifest to get available catalogs
                    var manifest = await client.GetManifestAsync(cancellationToken);
                    if (manifest?.Catalogs == null || manifest.Catalogs.Count == 0)
                    {
                        _logger.LogWarning("[Discover] No catalogs found in AIOStreams manifest");
                        return;
                    }

                    _logger.LogInformation("[Discover] Found {Count} catalogs in manifest", manifest.Catalogs.Count);

                    // Process each catalog
                    var totalItems = 0;
                    foreach (var catalogDef in manifest.Catalogs)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        if (string.IsNullOrWhiteSpace(catalogDef.Id) ||
                            string.IsNullOrWhiteSpace(catalogDef.Type))
                        {
                            continue;
                        }

                        try
                        {
                            var itemsAdded = await SyncCatalogAsync(client, catalogDef, cancellationToken);
                            totalItems += itemsAdded;
                            _logger.LogInformation("[Discover] Synced catalog {Id} ({Type}): {Count} items",
                                catalogDef.Id, catalogDef.Type, itemsAdded);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[Discover] Error syncing catalog {Id}", catalogDef.Id);
                        }
                    }

                    _logger.LogInformation("[Discover] Catalog sync complete: {Total} total items", totalItems);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[Discover] Catalog sync cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Discover] Error during catalog sync");
                throw;
            }
        }

        /// <summary>
        /// Syncs items from a single catalog endpoint.
        /// Paginates through all available pages (skip 0, 100, 200...) up to a cap of 500 items per catalog.
        /// </summary>
        private async Task<int> SyncCatalogAsync(
            AioStreamsClient client,
            AioStreamsCatalogDef catalogDef,
            CancellationToken cancellationToken)
        {
            // Clear previous items from this catalog source (scoped by type to avoid movie/series conflicts)
            await _db.ClearDiscoverCatalogBySourceAsync(catalogDef.Id!, catalogDef.Type);

            var itemsAdded = 0;
            const int maxItemsPerCatalog = 500;
            var skip = 0;

            while (itemsAdded < maxItemsPerCatalog)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // Fetch catalog items (paginated at 100 per page by Stremio spec)
                var response = await client.GetCatalogAsync(
                    catalogDef.Type!,
                    catalogDef.Id!,
                    searchQuery: null,
                    genre: null,
                    skip: skip,
                    cancellationToken: cancellationToken);

                if (response?.Metas == null || response.Metas.Count == 0)
                {
                    // Empty page means we've reached the end
                    break;
                }

                // Process each item in this page
                var pageItemsAdded = 0;
                foreach (var meta in response.Metas)
                {
                    if (cancellationToken.IsCancellationRequested || itemsAdded >= maxItemsPerCatalog)
                        break;

                    // Skip if missing required fields
                    if (string.IsNullOrWhiteSpace(meta.Id) && string.IsNullOrWhiteSpace(meta.ImdbId))
                        continue;

                    var imdbId = meta.ImdbId ?? meta.Id ?? "";
                    if (string.IsNullOrWhiteSpace(imdbId))
                        continue;

                    // Parse year from ReleaseInfo (format: "2022" or "2022–" for ongoing)
                    int? year = null;
                    if (!string.IsNullOrEmpty(meta.ReleaseInfo))
                    {
                        var yearStr = meta.ReleaseInfo.Split('–', '–')[0].Trim();
                        if (int.TryParse(yearStr, out var y))
                            year = y;
                    }

                    // Check if item is already in user library
                    var catalogItem = await _db.GetCatalogItemByImdbIdAsync(imdbId);
                    var isInLibrary = catalogItem != null;

                    // Parse IMDb rating (comes as string from JSON)
                    double? imdbRating = null;
                    if (!string.IsNullOrEmpty(meta.ImdbRating) && double.TryParse(meta.ImdbRating, out var rating))
                        imdbRating = rating;

                    // Join genres list into comma-separated string
                    var genres = meta.Genres != null && meta.Genres.Count > 0
                        ? string.Join(", ", meta.Genres)
                        : null;

                    // Create discover catalog entry
                    var animeEnabled = Plugin.Instance?.Configuration?.EnableAnimeLibrary ?? false;
                    var mediaType = NormalizeMediaType(meta.Type ?? catalogDef.Type ?? "movie", animeEnabled);
                    if (mediaType == null)
                    {
                        // Anime item filtered out (anime library disabled)
                        continue;
                    }

                    var entry = new DiscoverCatalogEntry
                    {
                        Id = $"aio:{catalogDef.Type}:{imdbId}",
                        ImdbId = imdbId,
                        Title = meta.Name ?? "",
                        Year = year,
                        MediaType = mediaType,
                        PosterUrl = meta.Poster,
                        BackdropUrl = meta.Background,
                        Overview = meta.Description,
                        Genres = genres,
                        ImdbRating = imdbRating,
                        CatalogSource = catalogDef.Id!,
                        AddedAt = DateTime.UtcNow.ToString("o"),
                        IsInUserLibrary = isInLibrary
                    };

                    await _db.UpsertDiscoverCatalogEntryAsync(entry);
                    itemsAdded++;
                    pageItemsAdded++;
                }

                // If we got fewer items than a full page (100), we've reached the end
                if (response.Metas.Count < 100)
                    break;

                skip += 100;
            }

            return itemsAdded;
        }

        /// <summary>
        /// Normalizes media type to standard values: "movie", "series", or "anime".
        /// When anime is disabled, <c>null</c> is returned for anime items (filtered out).
        /// </summary>
        private static string? NormalizeMediaType(string type, bool animeEnabled)
        {
            return type.ToLowerInvariant() switch
            {
                "series" or "tv" or "tvshow" or "tvshows" => "series",
                "anime" => animeEnabled ? "anime" : null,
                _ => "movie"
            };
        }

        /// <summary>
        /// Clears the discover catalog for a specific source before re-syncing.
        /// </summary>
        public async Task ClearSourceAsync(string catalogSource)
        {
            try
            {
                _logger.LogInformation("[Discover] Clearing catalog source: {Source}", catalogSource);
                await _db.ClearDiscoverCatalogBySourceAsync(catalogSource);
                _logger.LogInformation("[Discover] Source cleared");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Discover] Error clearing source {Source}", catalogSource);
                throw;
            }
        }
    }
}
