using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Controller.Library;
using InfiniteDrive.Models;
using InfiniteDrive.Logging;
using InfiniteDrive.Services;

namespace InfiniteDrive.Channels
{
    /// <summary>
    /// Manifest-driven IChannel: root folders from DB media types,
    /// catalog folders from manifest, genre subfolders from catalog extras.
    /// Browse-only. Items NOT in library (is_in_user_library = 0).
    /// </summary>
    public class InfiniteDriveDiscoverChannel : IChannel
    {
        private readonly ILogManager _logManager;
        private readonly IUserManager _userManager;

        // Manifest cache (1hr TTL)
        private static List<AioStreamsCatalogDef>? _cachedCatalogs;
        private static DateTime _catalogsFetchedAt = DateTime.MinValue;
        private static readonly TimeSpan CatalogCacheTtl = TimeSpan.FromHours(1);

        public InfiniteDriveDiscoverChannel(ILogManager logManager, IUserManager userManager)
        {
            _logManager = logManager;
            _userManager = userManager;
        }

        // ── IChannel Properties ──────────────────────────────────────────────

        public string Name => "InfiniteDrive Discover";
        public string Description => "Browse movies, series, and anime from AIOStreams";
        public string DataVersion => "1";
        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

        // ── GetChannelItems — 4-Path Router ─────────────────────────────────

        public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)
        {
            var plugin = Plugin.Instance;
            if (plugin?.DatabaseManager == null || plugin.Configuration == null)
                return Empty();

            var folderId = query.FolderId;

            try
            {
                if (string.IsNullOrEmpty(folderId))
                    return await GetRootFolders(plugin);
                if (folderId == "movie" || folderId == "series" || folderId == "anime")
                    return await GetCatalogFolders(plugin, folderId, ct);
                if (folderId.StartsWith("cat:", StringComparison.Ordinal))
                    return await GetCatalogContent(plugin, folderId);
                return Empty();
            }
            catch (Exception ex)
            {
                _logManager.GetLogger(Name).ErrorException("[InfiniteDrive] DiscoverChannel error", ex);
                return Empty();
            }
        }

        // ── Root Folders — dynamic from DB ──────────────────────────────────

        private async Task<ChannelItemResult> GetRootFolders(Plugin plugin)
        {
            var mediaTypes = await plugin.DatabaseManager.GetDiscoverMediaTypesAsync();
            var labelMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["movie"] = "Movies",
                ["series"] = "Series",
                ["anime"] = "Anime"
            };

            var items = mediaTypes
                .Where(t => labelMap.ContainsKey(t))
                .Select(t => new ChannelItemInfo
                {
                    Id = t.ToLowerInvariant(),
                    Name = labelMap[t],
                    Type = ChannelItemType.Folder,
                    Overview = $"Browse {labelMap[t].ToLowerInvariant()} from AIOStreams",
                    MediaType = ChannelMediaType.Video
                })
                .ToList();

            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }

        // ── Catalog Folders — from manifest ─────────────────────────────────

        private async Task<ChannelItemResult> GetCatalogFolders(Plugin plugin, string mediaType, CancellationToken ct)
        {
            var catalogs = await GetCachedCatalogsAsync(plugin, ct);
            var dbSources = await plugin.DatabaseManager.GetDiscoverCatalogSourcesAsync(mediaType);

            var matched = catalogs
                .Where(c => !string.IsNullOrEmpty(c.Id) && !string.IsNullOrEmpty(c.Type)
                    && string.Equals(c.Type, mediaType, StringComparison.OrdinalIgnoreCase))
                .Where(c => !RequiresSearchOnly(c))
                .Where(c => dbSources.Contains(c.Id, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var items = matched.Select(c => new ChannelItemInfo
            {
                Id = $"cat:{c.Id}",
                Name = c.Name ?? c.Id,
                Type = ChannelItemType.Folder,
                Overview = $"Browse {c.Name ?? c.Id}",
                MediaType = ChannelMediaType.Video
            }).ToList();

            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }

        // ── Catalog Content — genre subfolders or items ─────────────────────

        private async Task<ChannelItemResult> GetCatalogContent(Plugin plugin, string folderId)
        {
            // cat:{catalogId} or cat:{catalogId}:{genre}
            var parts = folderId.Split(':');
            if (parts.Length < 2)
                return Empty();

            var catalogId = parts[1];
            var genre = parts.Length >= 3 ? parts[2] : null;

            // If genre specified, return items filtered by genre
            if (genre != null)
                return await GetItemsForCatalog(plugin, catalogId, genre);

            // Check manifest for genre extras
            var catalogs = _cachedCatalogs ?? new List<AioStreamsCatalogDef>();
            var catalogDef = catalogs.FirstOrDefault(c =>
                string.Equals(c.Id, catalogId, StringComparison.OrdinalIgnoreCase));

            var genreExtra = catalogDef?.Extra?.FirstOrDefault(e =>
                string.Equals(e.Name, "genre", StringComparison.OrdinalIgnoreCase));

            if (genreExtra?.Options?.Count > 0)
            {
                // Return genre subfolders
                var items = genreExtra.Options.Select(g => new ChannelItemInfo
                {
                    Id = $"cat:{catalogId}:{g}",
                    Name = g,
                    Type = ChannelItemType.Folder,
                    MediaType = ChannelMediaType.Video
                }).ToList();

                return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
            }

            // No genre extra — return items directly
            return await GetItemsForCatalog(plugin, catalogId, null);
        }

        private async Task<ChannelItemResult> GetItemsForCatalog(Plugin plugin, string catalogSource, string? genre)
        {
            var entries = await plugin.DatabaseManager.GetDiscoverCatalogBySourceAsync(catalogSource, genre, 42, 0);

            var items = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.ImdbId))
                .Select(MapToChannelItem)
                .ToList();

            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }

        // ── Mapping — simplified (no ✓ decoration) ──────────────────────────

        private static ChannelItemInfo MapToChannelItem(DiscoverCatalogEntry entry)
        {
            return new ChannelItemInfo
            {
                Id = entry.ImdbId,
                Name = entry.Title,
                OriginalTitle = entry.Title,
                Type = ChannelItemType.Media,
                ImageUrl = entry.PosterUrl,
                Overview = entry.Overview,
                MediaType = ChannelMediaType.Video,
                ContentType = entry.MediaType is "series" or "anime"
                    ? ChannelMediaContentType.Episode
                    : ChannelMediaContentType.Movie,
                CommunityRating = entry.ImdbRating.HasValue
                    ? (float)(entry.ImdbRating.Value / 2.0)
                    : null,
                OfficialRating = entry.Certification,
                DateCreated = DateTime.TryParse(entry.AddedAt, out var dc) ? dc : (DateTime?)null
            };
        }

        // ── Manifest Cache ──────────────────────────────────────────────────

        private async Task<List<AioStreamsCatalogDef>> GetCachedCatalogsAsync(Plugin plugin, CancellationToken ct)
        {
            if (_cachedCatalogs != null && DateTime.UtcNow - _catalogsFetchedAt < CatalogCacheTtl)
                return _cachedCatalogs;

            try
            {
                using var client = new AioStreamsClient(plugin.Configuration, new EmbyLoggerAdapter<InfiniteDriveDiscoverChannel>(_logManager.GetLogger(Name)));
                var manifest = await client.GetManifestAsync(ct);
                _cachedCatalogs = manifest?.Catalogs ?? new List<AioStreamsCatalogDef>();
                _catalogsFetchedAt = DateTime.UtcNow;
            }
            catch
            {
                _cachedCatalogs ??= new List<AioStreamsCatalogDef>();
            }

            return _cachedCatalogs;
        }

        private static bool RequiresSearchOnly(AioStreamsCatalogDef catalog) =>
            catalog.Extra?.Any(e =>
                string.Equals(e.Name, "search", StringComparison.OrdinalIgnoreCase)
                && e.IsRequired) ?? false;

        // ── Helpers ─────────────────────────────────────────────────────────

        private static ChannelItemResult Empty() =>
            new() { Items = new List<ChannelItemInfo>(), TotalRecordCount = 0 };

        // ── Channel Image ───────────────────────────────────────────────────

        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            var stream = typeof(Plugin).Assembly
                .GetManifestResourceStream("InfiniteDrive.thumb.png");
            return Task.FromResult(new DynamicImageResponse { Stream = stream, Format = ImageFormat.Png });
        }

        public IEnumerable<ImageType> GetSupportedChannelImages()
        {
            return new[] { ImageType.Primary };
        }
    }
}
