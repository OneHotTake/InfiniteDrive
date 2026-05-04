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
    // A3 — /InfiniteDrive/DbStats
    // ════════════════════════════════════════════════════════════════════════════

    [Route("/InfiniteDrive/DbStats", "GET", Summary = "Returns SQLite database statistics for the health dashboard")]
    public class DbStatsRequest : IReturn<object> { }

    /// <summary>Admin-only DB stats endpoint.</summary>
    public class DbStatsService : IService, IRequiresRequest
    {
        private readonly IAuthorizationContext _authCtx;
        public IRequest Request { get; set; } = null!;

        public DbStatsService(IAuthorizationContext authCtx) { _authCtx = authCtx; }

        public async Task<object> Get(DbStatsRequest _)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var db = Plugin.Instance?.DatabaseManager;
            if (db == null) return new { Error = "Plugin not initialised" };

            var cacheStats    = await db.GetResolutionCacheStatsAsync();
            var coverageStats = await db.GetResolutionCoverageAsync();
            var dbPath        = db.GetDatabasePath();
            long dbBytes      = 0;
            // File size stat is non-critical — fail silently if file is locked
            try { dbBytes = new FileInfo(dbPath).Length; } catch { }

            return new
            {
                catalog_items    = new { total = coverageStats.TotalStrm, with_strm = coverageStats.TotalStrm, cached = coverageStats.ValidCached },
                resolution_cache = new { total = cacheStats.Total, valid = cacheStats.ValidUnexpired, stale = cacheStats.Stale, failed = cacheStats.Failed },
                database         = new { path = dbPath, size_mb = Math.Round(dbBytes / 1_048_576.0, 2) },
            };
        }
    }
}
