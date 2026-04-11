using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Tracks per-user per-rail state for home screen sections.
    /// Uses a marker pattern (via Subtitle field) for stable identity.
    /// </summary>
    public class HomeSectionTracker
    {
        /// <summary>
        /// Rail types supported by InfiniteDrive home screen rails.
        /// </summary>
        public enum RailType
        {
            Saved,
            TrendingMovies,
            TrendingSeries,
            NewThisWeek,
            AdminChosen
        }

        /// <summary>
        /// Section markers for each rail type. Used in the Subtitle field
        /// for stable identity across restarts and user sessions.
        /// </summary>
        public static readonly Dictionary<RailType, string> SectionMarkers = new()
        {
            { RailType.Saved, "embystreams_rail_saved" },
            { RailType.TrendingMovies, "embystreams_rail_trending_movies" },
            { RailType.TrendingSeries, "embystreams_rail_trending_series" },
            { RailType.NewThisWeek, "embystreams_rail_new_this_week" },
            { RailType.AdminChosen, "embystreams_rail_admin_chosen" }
        };

        private readonly DatabaseManager _db;
        private readonly ILogger<HomeSectionTracker> _logger;

        public HomeSectionTracker(DatabaseManager db, ILogger<HomeSectionTracker> logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Initializes tracking records for all existing users.
        /// Called on plugin installation.
        /// </summary>
        public Task InitializeForAllUsersAsync(CancellationToken ct = default)
        {
            // This method is called during plugin installation.
            // The UserManager will be injected in the actual implementation.
            // For now, this is a placeholder - actual user iteration happens in Plugin.cs.
            _logger.LogInformation("[HomeSectionTracker] InitializeForAllUsersAsync called");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Initializes tracking records for a specific user.
        /// Called when a new user is added.
        /// </summary>
        public async Task InitializeRailForUserAsync(string userId, IEnumerable<RailType> railTypes, CancellationToken ct = default)
        {
            foreach (var railType in railTypes)
            {
                var marker = SectionMarkers[railType];
                var tracking = new HomeSectionTracking
                {
                    UserId = userId,
                    RailType = railType.ToString().ToLowerInvariant(),
                    SectionMarker = marker
                };
                await _db.InsertHomeSectionTrackingAsync(tracking, ct);
            }
            _logger.LogInformation("[HomeSectionTracker] Initialized rails for user {UserId}", userId);
        }

        /// <summary>
        /// Gets the Emby-assigned section ID for a user's rail.
        /// </summary>
        public async Task<string?> GetSectionIdAsync(string userId, RailType railType, CancellationToken ct = default)
        {
            var tracking = await _db.GetHomeSectionTrackingAsync(userId, railType.ToString().ToLowerInvariant(), ct);
            return tracking?.EmbySectionId;
        }

        /// <summary>
        /// Tracks the Emby-assigned section ID for a user's rail.
        /// </summary>
        public async Task TrackSectionIdAsync(string userId, RailType railType, string sectionId, CancellationToken ct = default)
        {
            var tracking = await _db.GetHomeSectionTrackingAsync(userId, railType.ToString().ToLowerInvariant(), ct);
            if (tracking != null)
            {
                tracking.EmbySectionId = sectionId;
                tracking.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.UpdateHomeSectionTrackingAsync(tracking, ct);
            }
            else
            {
                tracking = new HomeSectionTracking
                {
                    UserId = userId,
                    RailType = railType.ToString().ToLowerInvariant(),
                    SectionMarker = SectionMarkers[railType],
                    EmbySectionId = sectionId
                };
                await _db.InsertHomeSectionTrackingAsync(tracking, ct);
            }
            _logger.LogInformation("[HomeSectionTracker] Tracked section {SectionId} for user {UserId} rail {Rail}", sectionId, userId, railType);
        }

        /// <summary>
        /// Checks if a rail is already tracked for a user.
        /// </summary>
        public async Task<bool> IsRailTrackedAsync(string userId, RailType railType, CancellationToken ct = default)
        {
            var tracking = await _db.GetHomeSectionTrackingAsync(userId, railType.ToString().ToLowerInvariant(), ct);
            return tracking != null && !string.IsNullOrEmpty(tracking.EmbySectionId);
        }
    }
}
