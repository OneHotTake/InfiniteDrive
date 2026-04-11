using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using InfiniteDrive.Repositories;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;

namespace InfiniteDrive.Services
{
    // ── Request / Response DTOs ──────────────────────────────────────────────────

    [Route("/InfiniteDrive/User/MyPins", "GET",
        Summary = "Returns all pins for the current user (playback + discover)")]
    public class GetUserPinsRequest : IReturn<GetUserPinsResponse> { }

    public class UserPinDto
    {
        public string CatalogItemId { get; set; } = string.Empty;
        public string? ImdbId { get; set; }
        public string Title { get; set; } = string.Empty;
        public int? Year { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public string PinnedAt { get; set; } = string.Empty;
        public string PinSource { get; set; } = string.Empty;
    }

    public class GetUserPinsResponse
    {
        public List<UserPinDto> PlaybackPins { get; set; } = new();
        public List<UserPinDto> DiscoverPins { get; set; } = new();
    }

    [Route("/InfiniteDrive/User/RemovePins", "POST",
        Summary = "Removes selected pins for the current user")]
    public class RemovePinsRequest : IReturn<RemovePinsResponse>
    {
        public List<string> CatalogItemIds { get; set; } = new();
    }

    public class RemovePinsResponse
    {
        public bool Success { get; set; }
        public int Count { get; set; }
    }

    // ── Service ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// User-facing endpoints for managing pinned items (My Picks).
    /// No admin guard — users can only see and manage their own pins.
    /// </summary>
    public class UserService : IService, IRequiresRequest
    {
        private readonly ILogger<UserService> _logger;
        private readonly DatabaseManager _db;
        private readonly UserPinRepository _pinRepo;
        private readonly IAuthorizationContext _authCtx;

        public IRequest Request { get; set; } = null!;

        public UserService(ILogManager logManager, IAuthorizationContext authCtx)
        {
            _logger  = new EmbyLoggerAdapter<UserService>(logManager.GetLogger("InfiniteDrive"));
            _db      = Plugin.Instance.DatabaseManager;
            _pinRepo = Plugin.Instance.UserPinRepository;
            _authCtx = authCtx;
        }

        /// <summary>
        /// Returns the Emby user ID from the current request, or throws if unauthenticated.
        /// </summary>
        private string GetCurrentUserId()
        {
            var user = _authCtx.GetAuthorizationInfo(Request).User;
            if (user == null)
                throw new UnauthorizedAccessException("Authentication required");
            return user.Id.ToString("N");
        }

        /// <summary>
        /// Handles <c>GET /InfiniteDrive/User/MyPins</c>.
        /// Returns playback-pinned and discover-pinned items for the current user.
        /// </summary>
        public async Task<object> Get(GetUserPinsRequest _)
        {
            try
            {
                var userId = GetCurrentUserId();
                var pins = await _pinRepo.GetPinsForUserAsync(userId, CancellationToken.None);

                if (pins.Count == 0)
                    return new GetUserPinsResponse();

                var catalogIds = pins.Select(p => p.CatalogItemId).Distinct().ToList();
                var items = await _db.GetCatalogItemsByIdsAsync(catalogIds, CancellationToken.None);
                var itemById = items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);

                UserPinDto? ToDto(InfiniteDrive.Models.UserItemPin pin)
                {
                    if (!itemById.TryGetValue(pin.CatalogItemId, out var item))
                        return null;
                    return new UserPinDto
                    {
                        CatalogItemId = item.Id,
                        ImdbId        = item.ImdbId,
                        Title         = item.Title,
                        Year          = item.Year,
                        MediaType     = item.MediaType,
                        PinnedAt      = pin.PinnedAt,
                        PinSource     = pin.PinSource
                    };
                }

                var playback = pins
                    .Where(p => p.PinSource == "playback")
                    .OrderByDescending(p => p.PinnedAt)
                    .Select(ToDto)
                    .Where(d => d != null)
                    .Cast<UserPinDto>()
                    .ToList();

                var discover = pins
                    .Where(p => p.PinSource == "discover")
                    .OrderByDescending(p => p.PinnedAt)
                    .Select(ToDto)
                    .Where(d => d != null)
                    .Cast<UserPinDto>()
                    .ToList();

                return new GetUserPinsResponse
                {
                    PlaybackPins = playback,
                    DiscoverPins = discover
                };
            }
            catch (UnauthorizedAccessException)
            {
                return new GetUserPinsResponse();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UserService] Failed to load user pins");
                return new GetUserPinsResponse();
            }
        }

        /// <summary>
        /// Handles <c>POST /InfiniteDrive/User/RemovePins</c>.
        /// Deletes pin records for the current user. Item remains until Deep Clean removes it.
        /// </summary>
        public async Task<object> Post(RemovePinsRequest req)
        {
            try
            {
                var userId = GetCurrentUserId();

                if (req.CatalogItemIds == null || req.CatalogItemIds.Count == 0)
                    return new RemovePinsResponse { Success = false, Count = 0 };

                foreach (var catalogItemId in req.CatalogItemIds)
                    await _pinRepo.RemovePinAsync(userId, catalogItemId, CancellationToken.None);

                _logger.LogInformation("[UserService] User {UserId} removed {Count} pins", userId, req.CatalogItemIds.Count);

                return new RemovePinsResponse { Success = true, Count = req.CatalogItemIds.Count };
            }
            catch (UnauthorizedAccessException)
            {
                return new RemovePinsResponse { Success = false, Count = 0 };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UserService] Failed to remove pins");
                return new RemovePinsResponse { Success = false, Count = 0 };
            }
        }
    }
}
