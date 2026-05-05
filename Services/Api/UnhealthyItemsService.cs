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
    // U1 — /InfiniteDrive/UnhealthyItems  (items currently stuck in failed state)
    // ════════════════════════════════════════════════════════════════════════════

    [Route("/InfiniteDrive/UnhealthyItems", "GET",
        Summary = "Admin: returns items currently stuck in a failed/unavailable resolution state")]
    public class UnhealthyItemsRequest : IReturn<object> { }

    /// <summary>
    /// Admin-only endpoint that surfaces catalog items whose resolution is currently
    /// cached as failed (no streams, network error, token expiry, etc.) and whose
    /// failure TTL has not yet expired.  Useful for identifying "consistently broken"
    /// items before users hit them during playback.
    /// </summary>
    public class UnhealthyItemsService : IService, IRequiresRequest
    {
        private readonly IAuthorizationContext _authCtx;
        public IRequest Request { get; set; } = null!;

        public UnhealthyItemsService(IAuthorizationContext authCtx) { _authCtx = authCtx; }

        public async Task<object> Get(UnhealthyItemsRequest _)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var db = Plugin.Instance?.DatabaseManager;
            if (db == null) return new { Error = "Plugin not initialised" };

            var items = await db.GetFailedItemsAsync(50);
            return new
            {
                count = items.Count,
                items = items.Select(i => new
                {
                    aio_id     = i.AioId,
                    title      = i.Title,
                    season     = i.Season,
                    episode    = i.Episode,
                    retry_after = i.ExpiresAt,
                }).ToList(),
            };
        }
    }
}
