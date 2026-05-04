using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;

namespace InfiniteDrive.Services
{
    // ════════════════════════════════════════════════════════════════════════════
    // A1 — /InfiniteDrive/RecentErrors
    // ════════════════════════════════════════════════════════════════════════════

    [Route("/InfiniteDrive/RecentErrors", "GET", Summary = "Returns the last 20 playback failures for the health dashboard")]
    public class RecentErrorsRequest : IReturn<object> { }

    /// <summary>Admin-only recent-errors endpoint — surfaces the last 20 failed play events.</summary>
    public class RecentErrorsService : IService, IRequiresRequest
    {
        private readonly IAuthorizationContext _authCtx;
        public IRequest Request { get; set; } = null!;

        public RecentErrorsService(IAuthorizationContext authCtx) { _authCtx = authCtx; }

        public async Task<object> Get(RecentErrorsRequest _)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var db = Plugin.Instance?.DatabaseManager;
            if (db == null) return new { Error = "Plugin not initialised" };

            var entries = await db.GetRecentPlaybackAsync(20);
            var errors  = entries
                .Where(e => !string.IsNullOrEmpty(e.ErrorMessage))
                .Select(e => new
                {
                    imdb_id    = e.ImdbId,
                    title      = e.Title,
                    season     = e.Season,
                    episode    = e.Episode,
                    error      = e.ErrorMessage,
                    client     = e.ClientType,
                    played_at  = e.PlayedAt,
                })
                .ToList();

            return new { count = errors.Count, errors };
        }
    }
}
