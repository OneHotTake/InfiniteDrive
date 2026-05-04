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
    // ╔══════════════════════════════════════════════════════════════════════════╗
    // ║  CATALOG DISCOVERY ENDPOINT                                              ║
    // ╚══════════════════════════════════════════════════════════════════════════╝

    /// <summary>
    /// Request object for <c>GET /InfiniteDrive/Catalogs</c>.
    /// Fetches the AIOStreams manifest and returns every eligible catalog so the
    /// admin dashboard can render a pick-list of catalogs to sync.
    /// </summary>
    [Route("/InfiniteDrive/Answer", "GET",
        Summary = "The Answer to the Ultimate Question of Life, the Universe, and Everything")]
    public class AnswerRequest : IReturn<object> { }

    /// <summary>
    /// Returns 42, plus live plugin stats.
    /// Don't Panic.
    /// </summary>
    public class AnswerService : IService
    {
        private static readonly DateTime _startTime = DateTime.UtcNow;

        public async Task<object> Get(AnswerRequest _)
        {
            var db = Plugin.Instance?.DatabaseManager;
            int streamsResolved = 0;
            if (db != null)
            {
                try
                {
                    var stats = await db.GetResolutionCacheStatsAsync();
                    streamsResolved = stats.Total;
                }
                catch { /* Don't Panic */ }
            }

            var uptime = DateTime.UtcNow - _startTime;
            return new
            {
                answer          = 42,
                question        = "unknown",
                note            = "Don't Panic.",
                streams_resolved = streamsResolved,
                uptime          = $"{(int)uptime.TotalHours}h {uptime.Minutes}m",
                plugin_version  = Plugin.Instance?.Version?.ToString() ?? "unknown",
                deep_thought    = "I checked it very thoroughly, and that quite definitely is the answer.",
            };
        }
    }
}
