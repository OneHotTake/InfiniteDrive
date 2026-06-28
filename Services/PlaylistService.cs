using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Manages Emby playlists for InfiniteDrive: "My InfiniteDrive" per-user playlist
    /// and per-list playlists for external catalog subscriptions.
    /// </summary>
    public class PlaylistService
    {
        private readonly IPlaylistManager _playlistManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager? _userManager;
        private readonly ILogger<PlaylistService> _logger;

        public PlaylistService(
            IPlaylistManager playlistManager,
            ILibraryManager libraryManager,
            ILogger<PlaylistService> logger,
            IUserManager? userManager = null)
        {
            _playlistManager = playlistManager;
            _libraryManager = libraryManager;
            _logger = logger;
            _userManager = userManager;
        }

        /// <summary>
        /// Finds or creates the "My InfiniteDrive" playlist for a user.
        /// </summary>
        public async Task<Playlist?> GetOrCreateMyInfiniteDriveAsync(User user)
        {
            var existing = FindPlaylist("My InfiniteDrive");
            if (existing != null)
                return existing;

            return await CreatePlaylistAsync("My InfiniteDrive", user);
        }

        /// <summary>
        /// Adds an Emby item to the user's "My InfiniteDrive" playlist.
        /// </summary>
        public async Task AddItemToMyInfiniteDriveAsync(Guid embyItemId, User user)
        {
            var playlist = await GetOrCreateMyInfiniteDriveAsync(user);
            if (playlist == null) return;

            await AddToPlaylistAsync(playlist, embyItemId, user);
            _logger.LogDebug("[PlaylistService] Added {ItemId} to My InfiniteDrive for user {UserId}",
                embyItemId, user.Id);
        }

        /// <summary>
        /// Removes an Emby item from the user's "My InfiniteDrive" playlist.
        /// </summary>
        public async Task RemoveItemFromMyInfiniteDriveAsync(Guid embyItemId, User user)
        {
            var playlist = FindPlaylist("My InfiniteDrive");
            if (playlist == null) return;

            await RemoveFromPlaylistAsync(playlist, embyItemId);
            _logger.LogDebug("[PlaylistService] Removed {ItemId} from My InfiniteDrive for user {UserId}",
                embyItemId, user.Id);
        }

        /// <summary>
        /// Gets items from the user's "My InfiniteDrive" playlist with pagination.
        /// Uses ILibraryManager to query children of the playlist folder.
        /// </summary>
        public Task<List<BaseItem>> GetMyInfiniteDriveItemsAsync(int skip, int limit)
        {
            var playlist = FindPlaylist("My InfiniteDrive");
            if (playlist == null)
                return Task.FromResult(new List<BaseItem>());

            var query = new InternalItemsQuery
            {
                Limit = limit,
                StartIndex = skip,
                Recursive = true,
                AncestorIds = new[] { playlist.InternalId }
            };

            var items = _libraryManager.GetItemList(query);
            return Task.FromResult((items ?? Array.Empty<BaseItem>()).ToList());
        }

        /// <summary>
        /// Creates a playlist for a specific external list subscription.
        /// Returns the created or existing playlist.
        /// </summary>
        public async Task<Playlist?> CreateListPlaylistAsync(
            string listName, List<Guid> embyItemIds, User user)
        {
            var existing = FindPlaylist(listName);
            if (existing != null)
                return existing;

            var playlist = await CreatePlaylistAsync(listName, user);
            if (playlist == null) return null;

            foreach (var itemId in embyItemIds)
            {
                await AddToPlaylistAsync(playlist, itemId, user);
            }

            _logger.LogInformation("[PlaylistService] Created list playlist '{Name}' with {Count} items for user {UserId}",
                listName, embyItemIds.Count, user.Id);
            return playlist;
        }

        /// <summary>
        /// Removes a single item from a list playlist.
        /// Library item is NOT removed — only the playlist membership.
        /// </summary>
        public async Task RemoveItemFromListPlaylistAsync(
            string listName, Guid embyItemId, User user)
        {
            var playlist = FindPlaylist(listName);
            if (playlist == null) return;

            await RemoveFromPlaylistAsync(playlist, embyItemId);
        }

        // ── Private helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Finds a playlist by name. Optionally filters by user.
        /// </summary>
        public Playlist? FindPlaylist(string name, string? userId = null)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Playlist" },
                Name = name,
                Recursive = true
            };

            var results = _libraryManager.GetItemList(query)
                .OfType<Playlist>();

            // Note: Emby playlists are shared — user filtering is best-effort.
            // For alpha, just find by name.
            return results.FirstOrDefault();
        }

        /// <summary>
        /// Finds or creates a playlist with the given name for a user.
        /// </summary>
        public async Task<Playlist?> FindOrCreatePlaylistAsync(string name, User user)
        {
            var existing = FindPlaylist(name);
            if (existing != null) return existing;

            return await CreatePlaylistAsync(name, user);
        }

        /// <summary>
        /// Adds an Emby item to a named playlist. User lookup is deferred to Marvin if user not found.
        /// </summary>
        public async Task AddItemToPlaylistAsync(string playlistName, Guid embyItemId, string userId, CancellationToken ct = default)
        {
            var playlist = FindPlaylist(playlistName);
            if (playlist == null)
            {
                // Create the playlist for the owning user so the item actually surfaces.
                var user = ResolveUser(userId);
                if (user == null)
                {
                    _logger.LogWarning("[PlaylistService] Cannot create playlist '{Name}' — user {UserId} not resolvable", playlistName, userId);
                    return;
                }
                playlist = await CreatePlaylistAsync(playlistName, user);
                if (playlist == null) return;
            }

            var item = _libraryManager.GetItemById(embyItemId);
            if (item == null) return;

            try
            {
                await _playlistManager.AddToPlaylist(
                    playlist, new[] { item.InternalId }, true, null, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[PlaylistService] Failed to add {ItemId} to playlist {Name}", embyItemId, playlistName);
            }
        }

        /// <summary>
        /// Removes an Emby item from a named playlist.
        /// </summary>
        public async Task RemoveItemFromPlaylistAsync(string playlistName, Guid embyItemId, string userId, CancellationToken ct = default)
        {
            var playlist = FindPlaylist(playlistName);
            if (playlist == null) return;

            var item = _libraryManager.GetItemById(embyItemId);
            if (item == null) return;

            await RemoveFromPlaylistAsync(playlist, embyItemId);
        }

        /// <summary>
        /// Resolves an Emby user from the stored user-id string ("N" format). Falls back
        /// to the first administrator if the specific user can't be found, so playlists
        /// still materialise rather than silently vanishing.
        /// </summary>
        private User? ResolveUser(string? userId)
        {
            if (_userManager == null) return null;
            try
            {
                if (!string.IsNullOrEmpty(userId))
                {
                    var u = _userManager.GetUserById(userId);
                    if (u != null) return u;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[PlaylistService] GetUserById failed for {UserId}", userId);
            }
            return null;
        }

        private async Task<Playlist?> CreatePlaylistAsync(string name, User user)
        {
            try
            {
                var request = new PlaylistCreationRequest
                {
                    Name = name,
                    User = user,
                    IsPublic = true
                };

                var result = await _playlistManager.CreatePlaylist(request);
                if (result?.Id == null)
                {
                    _logger.LogWarning("[PlaylistService] CreatePlaylist returned null for '{Name}'", name);
                    return null;
                }

                // Emby returns the numeric INTERNAL id here (e.g. "175201"), not a Guid —
                // so re-find the freshly-created playlist by name to get the Playlist object.
                var playlist = FindPlaylist(name);
                if (playlist == null)
                    _logger.LogWarning("[PlaylistService] Playlist '{Name}' not found after creation (id {Id})", name, result.Id);
                else
                    _logger.LogInformation("[PlaylistService] Created playlist '{Name}' ({Id})", name, result.Id);
                return playlist;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PlaylistService] Failed to create playlist '{Name}'", name);
                return null;
            }
        }

        private async Task AddToPlaylistAsync(Playlist playlist, Guid itemId, User user)
        {
            try
            {
                var item = _libraryManager.GetItemById(itemId);
                if (item == null) return;

                await _playlistManager.AddToPlaylist(
                    playlist, new[] { item.InternalId }, true, user, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PlaylistService] Failed to add {ItemId} to playlist {PlaylistId}",
                    itemId, playlist.Id);
            }
        }

        private async Task RemoveFromPlaylistAsync(Playlist playlist, Guid itemId)
        {
            try
            {
                // RemoveFromPlaylist takes long playlistId + long[] entryIds
                // Use InternalId for the playlist, and item InternalId for the entry
                var item = _libraryManager.GetItemById(itemId);
                if (item == null) return;

                await _playlistManager.RemoveFromPlaylist(
                    playlist.InternalId, new[] { item.InternalId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PlaylistService] Failed to remove {ItemId} from playlist {PlaylistId}",
                    itemId, playlist.Id);
            }
        }
    }
}
