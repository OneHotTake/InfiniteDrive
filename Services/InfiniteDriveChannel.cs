using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Emby channel exposing user's Lists and Saved items.
    /// Auto-discovered by Emby — no Plugin.cs registration needed.
    /// </summary>
    public class InfiniteDriveChannel : IChannel
    {
        private readonly Microsoft.Extensions.Logging.ILogger _logger;
        private readonly DatabaseManager _db;
        private readonly IUserManager _userManager;

        public string Name => "InfiniteDrive";
        public string Description => "Browse your lists and saved items.";
        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

        public InfiniteDriveChannel(ILogManager logManager, IUserManager userManager)
        {
            _logger = new EmbyLoggerAdapter<InfiniteDriveChannel>(logManager.GetLogger("InfiniteDrive.Channel"));
            _db = Plugin.Instance.DatabaseManager;
            _userManager = userManager;
        }

        // ── IChannel ──────────────────────────────────────────────────────────

        public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            var folderId = query.FolderId;
            var userId = ResolveUserId(query.UserId);

            if (string.IsNullOrEmpty(folderId))
            {
                return Task.FromResult(GetRootFolders());
            }

            return folderId switch
            {
                "lists" => GetListsFolder(userId, cancellationToken),
                "saved" => GetSavedFolder(userId, cancellationToken),
                _ when folderId.StartsWith("list:") => GetListItems(folderId.Substring(5), cancellationToken),
                _ => Task.FromResult(new ChannelItemResult { Items = new List<ChannelItemInfo>() })
            };
        }

        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("InfiniteDrive channel does not provide custom images.");
        }

        public IEnumerable<ImageType> GetSupportedChannelImages() => Array.Empty<ImageType>();

        // ── Root ──────────────────────────────────────────────────────────────

        private static ChannelItemResult GetRootFolders()
        {
            return new ChannelItemResult
            {
                Items = new List<ChannelItemInfo>
                {
                    new()
                    {
                        Name = "Lists",
                        Id = "lists",
                        Type = ChannelItemType.Folder,
                        FolderType = ChannelFolderType.Container,
                        MediaType = ChannelMediaType.Video
                    },
                    new()
                    {
                        Name = "Saved",
                        Id = "saved",
                        Type = ChannelItemType.Folder,
                        FolderType = ChannelFolderType.Container,
                        MediaType = ChannelMediaType.Video
                    }
                },
                TotalRecordCount = 2
            };
        }

        // ── Lists ─────────────────────────────────────────────────────────────

        private async Task<ChannelItemResult> GetListsFolder(string? userId, CancellationToken ct)
        {
            var items = new List<ChannelItemInfo>();

            // Admin sees all enabled sources; non-admin sees only their own user catalogs
            var isAdmin = IsAdminUser(userId);

            if (isAdmin)
            {
                var sources = await _db.GetEnabledSourcesAsync(ct);
                foreach (var source in sources)
                {
                    items.Add(new ChannelItemInfo
                    {
                        Name = source.Name,
                        Id = "list:" + source.Id,
                        Type = ChannelItemType.Folder,
                        FolderType = ChannelFolderType.Container,
                        MediaType = ChannelMediaType.Video
                    });
                }
            }

            // Show user's own catalogs
            if (!string.IsNullOrEmpty(userId))
            {
                var userCatalogs = await _db.GetUserCatalogsByOwnerAsync(userId, true, ct);
                foreach (var catalog in userCatalogs)
                {
                    items.Add(new ChannelItemInfo
                    {
                        Name = catalog.DisplayName,
                        Id = "list:" + catalog.Id,
                        Type = ChannelItemType.Folder,
                        FolderType = ChannelFolderType.Container,
                        MediaType = ChannelMediaType.Video
                    });
                }
            }

            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }

        // ── Saved ─────────────────────────────────────────────────────────────

        private async Task<ChannelItemResult> GetSavedFolder(string? userId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(userId))
            {
                return new ChannelItemResult { Items = new List<ChannelItemInfo>() };
            }

            var savedItems = await _db.GetSavedItemsByUserAsync(userId, ct);
            var items = new List<ChannelItemInfo>(savedItems.Count);

            foreach (var item in savedItems)
            {
                var info = new ChannelItemInfo
                {
                    Name = item.Title,
                    Id = "media:" + item.Id,
                    Type = ChannelItemType.Media,
                    MediaType = ChannelMediaType.Video,
                    ProductionYear = item.Year,
                    Overview = $"Status: {item.Status}"
                };

                if (item.PrimaryId.Type == Models.MediaIdType.Imdb)
                {
                    info.ProviderIds = new ProviderIdDictionary { ["imdb"] = item.PrimaryId.Value };
                }

                items.Add(info);
            }

            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }

        // ── List Items ────────────────────────────────────────────────────────

        private async Task<ChannelItemResult> GetListItems(string listId, CancellationToken ct)
        {
            // Try as a source first, then as a user catalog
            // For now, return discover catalog items associated with this list
            var entries = await _db.GetDiscoverCatalogAsync(200, 0);
            var filtered = entries.Where(e => e.CatalogSource == listId).ToList();

            var items = new List<ChannelItemInfo>(filtered.Count);
            foreach (var entry in filtered)
            {
                var info = new ChannelItemInfo
                {
                    Name = entry.Title,
                    Id = "media:" + entry.ImdbId,
                    Type = ChannelItemType.Media,
                    MediaType = ChannelMediaType.Video,
                    ProductionYear = entry.Year,
                    Overview = entry.Overview,
                    ImageUrl = entry.PosterUrl
                };

                if (!string.IsNullOrEmpty(entry.ImdbId))
                {
                    info.ProviderIds = new ProviderIdDictionary { ["imdb"] = entry.ImdbId };
                }

                items.Add(info);
            }

            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private string? ResolveUserId(long numericUserId)
        {
            try
            {
                var user = _userManager.GetUserById(numericUserId);
                return user?.Id.ToString("N");
            }
            catch
            {
                return null;
            }
        }

        private bool IsAdminUser(string? userId)
        {
            if (string.IsNullOrEmpty(userId)) return false;
            try
            {
                var user = _userManager.GetUserById(userId);
                return user?.Policy?.IsAdministrator ?? false;
            }
            catch
            {
                return false;
            }
        }
    }
}
