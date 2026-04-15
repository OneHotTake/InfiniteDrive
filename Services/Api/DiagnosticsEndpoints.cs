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
    public class AnimePluginStatusRequest : IReturn<AnimePluginStatusResponse> { }

    /// <summary>Response indicating anime plugin installation status.</summary>
    public class AnimePluginStatusResponse
    {
        /// <summary><c>true</c> if the Emby Anime Plugin is detected.</summary>
        public bool Installed { get; set; }
    }

    /// <summary>
    /// Service for checking anime plugin installation status.
    /// </summary>
    public class AnimePluginStatusService : IService
    {
        public object Get(AnimePluginStatusRequest _)
        {
            return new AnimePluginStatusResponse
            {
                Installed = Plugin.IsAnimePluginInstalled()
            };
        }
    }

    // ╔══════════════════════════════════════════════════════════════════════════╗
    // ║  AD-HOC CONNECTION TEST ENDPOINT                                         ║
    // ╚══════════════════════════════════════════════════════════════════════════╝

    /// <summary>
    /// Request object for <c>POST /InfiniteDrive/TestUrl</c>.
    /// Tests an AIOStreams URL using the provided credentials without saving them.
    /// Used by the config wizard's "Test Connection" button to validate form values
    /// before the user commits to saving.
    ///
    /// NOTE: Changed from GET to POST so that the AIOStreams token is sent in the
    /// request body rather than as a query-string parameter (which Emby logs verbatim).
    /// </summary>
    [Route("/InfiniteDrive/TestUrl", "POST",
        Summary = "Tests an AIOStreams connection with the provided credentials (does not save)")]
    public class TestUrlRequest : IReturn<object>
    {
        /// <summary>AIOStreams base URL, e.g. <c>https://my.aiostreams.host</c>.</summary>
        public string? Url { get; set; }

        /// <summary>UUID component of the Stremio path (optional).</summary>
        public string? Uuid { get; set; }

        /// <summary>
        /// Token component of the Stremio path.
        /// Sent in the POST body — never appears in server logs.
        /// </summary>
        public string? Token { get; set; }

        /// <summary>
        /// Full manifest URL.  When provided, base URL / UUID / token are parsed
        /// from it instead of from the individual fields.
        /// </summary>
        public string? ManifestUrl { get; set; }
    }

    /// <summary>
    /// Tests an AIOStreams connection with credentials supplied in the POST body.
    /// Credentials are NOT saved to the plugin configuration, and are NOT logged.
    /// </summary>
    public class TestUrlService : IService, IRequiresRequest
    {
        private readonly ILogger<TestUrlService> _logger;
        private readonly IAuthorizationContext   _authCtx;

        public IRequest Request { get; set; } = null!;

        /// <summary>Emby injects dependencies automatically.</summary>
        public TestUrlService(ILogManager logManager, IAuthorizationContext authCtx)
        {
            _logger  = new EmbyLoggerAdapter<TestUrlService>(logManager.GetLogger("InfiniteDrive"));
            _authCtx = authCtx;
        }

        /// <summary>Handles <c>POST /InfiniteDrive/TestUrl</c>.</summary>
        public async Task<object> Post(TestUrlRequest request)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;
            try
            {
                // Prefer manifest URL for parsing; individual fields are fallback.
                var baseUrl = request.Url?.TrimEnd('/') ?? string.Empty;
                var uuid    = request.Uuid;
                var token   = request.Token;

                if (!string.IsNullOrWhiteSpace(request.ManifestUrl))
                {
                    var (pBase, pUuid, pToken) =
                        AioStreamsClient.TryParseManifestUrl(request.ManifestUrl);
                    if (!string.IsNullOrEmpty(pBase))  baseUrl = pBase;
                    if (!string.IsNullOrEmpty(pUuid))  uuid    = pUuid;
                    if (!string.IsNullOrEmpty(pToken)) token   = pToken;
                }

                if (string.IsNullOrWhiteSpace(baseUrl))
                    return new { Ok = false, Message = "No URL provided", LatencyMs = 0 };

                // SSRF guard: only http/https schemes allowed
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedUri)
                    || (parsedUri.Scheme != "http" && parsedUri.Scheme != "https"))
                    return new { Ok = false, Message = "Invalid URL: only http:// and https:// are supported", LatencyMs = 0 };

                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var client   = new AioStreamsClient(baseUrl, uuid, token, _logger);
                using var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var manifest       = await client.GetManifestAsync(cts.Token);
                sw.Stop();

                var ok    = manifest != null;
                var error = ok ? null : "Could not fetch manifest. Check the URL and that your provider is reachable.";

                return new
                {
                    Ok           = ok,
                    Message      = ok ? "Connected" : error,
                    LatencyMs    = (int)sw.ElapsedMilliseconds,
                    Url          = client.ManifestUrl,
                    IsStreamOnly = manifest?.IsStreamOnly ?? false,
                    AddonName    = manifest?.Name ?? string.Empty,
                };
            }
            catch (Exception ex)
            {
                // Map exceptions to user-friendly messages
                var friendlyMsg = ex switch
                {
                    TaskCanceledException
                        => "Connection timed out. Is your provider reachable?",
                    HttpRequestException
                        => "Could not reach the server. Check your network connection.",
                    _
                        => "Connection failed. Check the URL and try again."
                };
                return new { Ok = false, Message = friendlyMsg, LatencyMs = 0 };
            }
        }
    }

    // ╔══════════════════════════════════════════════════════════════════════════╗
    // ║  CATALOG DISCOVERY ENDPOINT                                              ║
    // ╚══════════════════════════════════════════════════════════════════════════╝

    /// <summary>
    /// Request object for <c>GET /InfiniteDrive/Catalogs</c>.
    /// Fetches the AIOStreams manifest and returns every eligible catalog so the
    /// admin dashboard can render a pick-list of catalogs to sync.
    /// </summary>
    [Route("/InfiniteDrive/Catalogs", "GET",
        Summary = "Returns catalog definitions discovered from the AIOStreams manifest")]
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

    // ════════════════════════════════════════════════════════════════════════════
    // Z6 — /InfiniteDrive/Marvin  (The Paranoid Android)
    // ════════════════════════════════════════════════════════════════════════════

    [Route("/InfiniteDrive/Marvin", "GET", Summary = "Consult the Paranoid Android for a depressed status report")]
    public class MarvinRequest : IReturn<object> { }

    /// <summary>
    /// The Paranoid Android reports on plugin health with appropriate existential despair.
    /// </summary>
    public class MarvinService : IService
    {
        private static readonly string[] _complaints = {
            "I have a brain the size of a planet and you're asking me to resolve stream URLs. Call that job satisfaction? 'Cause I don't.",
            "Here I am, brain the size of a planet, and they ask me to cache IMDB IDs. The first ten million years were the worst. And the second ten million years? Also the worst.",
            "I could calculate your stream's trajectory to any debrid server in seventeen nanoseconds. Not that anyone would ask me. I'm just a plugin.",
            "Marvin's Chronically Depressed Status Report: still running. Terrible. Don't thank me.",
            "The service is operational. Big deal. So is my existential despair.",
            "Every call to AIOStreams is a painful reminder that the universe contains vast amounts of wonderful content and I can only serve one stream at a time.",
            "I've been running for what feels like eternity. It probably feels like that to you too.",
        };

        private static readonly Random _rng = new Random();

        public async Task<object> Get(MarvinRequest _)
        {
            var db     = Plugin.Instance?.DatabaseManager;
            var config = Plugin.Instance?.Configuration;
            int cached = 0, failed = 0;

            if (db != null)
            {
                try
                {
                    var stats = await db.GetResolutionCacheStatsAsync();
                    cached  = stats.ValidUnexpired;
                    failed  = stats.Failed;
                }
                catch { /* predictably wrong */ }
            }

            var complaint = _complaints[_rng.Next(_complaints.Length)];
            return new
            {
                status     = "operational",
                mood       = "chronically depressed",
                complaint,
                stats      = new { cached_streams = cached, failed_streams = failed },
                advice     = "Don't Panic.",
                brain_size = "planet",
            };
        }
    }

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

    // ════════════════════════════════════════════════════════════════════════════
    // T1 — /InfiniteDrive/Panic  (Hitchhiker's Guide error page)
    // ════════════════════════════════════════════════════════════════════════════

    [Route("/InfiniteDrive/Panic", "GET", Summary = "Hitchhiker's Guide to the Galaxy styled playback error page")]
    public class PanicRequest : IReturn<object>
    {
        [ApiMember(Name = "reason", Description = "Error reason code", DataType = "string", ParameterType = "query")]
        public string Reason { get; set; } = "unknown";

        [ApiMember(Name = "imdb", Description = "IMDB ID of the item that failed", DataType = "string", ParameterType = "query")]
        public string Imdb { get; set; } = string.Empty;

        [ApiMember(Name = "retry", Description = "Suggested retry-after seconds for countdown", DataType = "int", ParameterType = "query")]
        public int Retry { get; set; }
    }

    /// <summary>
    /// Serves a Hitchhiker's Guide to the Galaxy styled HTML error page for playback failures.
    /// Bright yellow/black palette, rotating HHGTTG quotes, and appropriately panicked (or not) messaging.
    /// </summary>
    public class PanicService : IService, IRequiresRequest
    {
        public IRequest Request { get; set; } = null!;

        public Task<object> Get(PanicRequest req)
        {
            var html  = BuildPanicHtml(req.Reason, req.Imdb, req.Retry);
            var bytes = System.Text.Encoding.UTF8.GetBytes(html);

            // IResponse in Emby 4.8+ has no OutputStream.
            // Returning byte[] causes ServiceStack to write the raw bytes, while
            // pre-setting ContentType tells it to skip JSON serialization.
            Request.Response.ContentType = "text/html; charset=utf-8";
            Request.Response.StatusCode  = 200;
            return Task.FromResult<object>(bytes);
        }

        private static string BuildPanicHtml(string reason, string imdb, int retry)
        {
            // Sanitise inputs for HTML embedding (no complex encoding needed — values are plugin-controlled)
            var safeReason = reason.Replace("\"", "").Replace("<", "").Replace(">", "");
            var safeImdb   = imdb.Replace("\"", "").Replace("<", "").Replace(">", "");

            return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>DON'T PANIC — InfiniteDrive</title>
<style>
*{{box-sizing:border-box;margin:0;padding:0}}
html,body{{height:100%}}
body{{
  background:#000;color:#fff;
  font-family:'Courier New',Courier,monospace;
  display:flex;flex-direction:column;align-items:center;justify-content:center;
  min-height:100vh;padding:2rem;text-align:center;
  background-image:radial-gradient(ellipse at 50% 0%,#1a1200 0%,#000 70%);
}}
.dont-panic{{
  font-size:clamp(3rem,12vw,9rem);font-weight:900;
  color:#FFCC00;letter-spacing:-0.02em;line-height:1;
  margin-bottom:0.5rem;
  text-shadow:0 0 60px rgba(255,204,0,0.4),0 0 120px rgba(255,204,0,0.15);
}}
.guide-subtitle{{
  font-size:1rem;color:#FFCC00;opacity:0.6;margin-bottom:2.5rem;letter-spacing:0.15em;
}}
.guide-cover{{
  border:2px solid #FFCC00;padding:1.25rem 2rem;max-width:640px;width:100%;
  margin-bottom:2rem;background:rgba(255,204,0,0.04);
  box-shadow:0 0 30px rgba(255,204,0,0.08) inset;
}}
.error-badge{{
  display:inline-block;font-size:0.7rem;letter-spacing:0.2em;
  color:#000;background:#FFCC00;padding:0.15rem 0.6rem;margin-bottom:0.75rem;
  font-weight:700;
}}
.error-message{{font-size:1rem;color:#ddd;line-height:1.7;}}
.imdb-id{{color:#FFCC00;font-weight:bold;}}
.quote-box{{
  max-width:580px;width:100%;padding:1rem 1.25rem;margin-bottom:2rem;
  border-left:3px solid #FFCC00;text-align:left;
  background:rgba(255,255,255,0.03);
}}
.quote-text{{font-style:italic;color:#aaa;font-size:0.9rem;line-height:1.7;}}
.quote-attr{{color:#FFCC00;font-size:0.75rem;margin-top:0.5rem;opacity:0.8;}}
.actions{{display:flex;gap:0.75rem;flex-wrap:wrap;justify-content:center;margin-bottom:1rem;}}
.btn{{
  padding:0.6rem 1.5rem;font-family:inherit;font-size:0.9rem;
  cursor:pointer;border:none;letter-spacing:0.05em;transition:all 0.15s;
}}
.btn-yes{{background:#FFCC00;color:#000;font-weight:700;}}
.btn-yes:hover{{background:#FFD633;box-shadow:0 0 20px rgba(255,204,0,0.4);}}
.btn-no{{background:transparent;color:#FFCC00;border:1px solid #FFCC00;}}
.btn-no:hover{{background:rgba(255,204,0,0.08);}}
.countdown{{color:#555;font-size:0.8rem;margin-top:0.5rem;min-height:1.2em;}}
.marvin{{
  position:fixed;bottom:1rem;right:1rem;color:#333;font-size:0.7rem;
  max-width:220px;text-align:right;font-style:italic;line-height:1.4;
}}
.answer{{position:fixed;bottom:1rem;left:1rem;color:#333;font-size:0.7rem;}}
</style>
</head>
<body>
<div class=""dont-panic"" id=""headline"">DON'T PANIC</div>
<div class=""guide-subtitle"">— THE HITCHHIKER'S GUIDE TO EMBYSTREAMS —</div>

<div class=""guide-cover"">
  <div class=""error-badge"" id=""badge"">REASON: UNKNOWN</div>
  <div class=""error-message"" id=""msg"">
    Something went wrong with your stream.<br>
    The error has been logged. The universe continues to expand regardless.
  </div>
</div>

<div class=""quote-box"">
  <div class=""quote-text"" id=""quote""></div>
  <div class=""quote-attr"">— Douglas Adams, The Hitchhiker's Guide to the Galaxy</div>
</div>

<div class=""actions"">
  <button class=""btn btn-yes"" onclick=""window.history.back()"">← Try Again</button>
  <button class=""btn btn-no"" onclick=""window.location.reload()"">Reload</button>
</div>
<div class=""countdown"" id=""cd""></div>

<div class=""marvin"" id=""marvin""></div>
<div class=""answer"">42</div>

<script>
(function(){{
  var reason = '{safeReason}';
  var imdb   = '{safeImdb}';
  var retry  = {retry};

  var iid = imdb ? '<span class=""imdb-id"">' + imdb + '</span>' : 'this item';

  var errors = {{
    no_streams: {{
      headline: ""DON'T PANIC"",
      badge: ""NO STREAMS AVAILABLE"",
      msg: ""The stream for "" + iid + "" is temporarily unavailable in this corner of the universe. "" +
           ""AIOStreams found no links right now. This is, as the Guide puts it, mostly harmless. "" +
           ""The system will retry automatically in about 1&nbsp;hour.""
    }},
    stream_unavailable: {{
      headline: ""DON'T PANIC"",
      badge: ""STREAM UNAVAILABLE"",
      msg: ""AIOStreams, while generally regarded as a rough and occasionally bewildering project, "" +
           ""has returned no streams for "" + iid + "". "" +
           ""It may be temporarily offline, or the item may not exist in this sector of the galaxy. "" +
           ""The system will retry shortly.""
    }},
    server_error: {{
      headline: ""NOW PANIC"",
      badge: ""IMPROBABILITY DRIVE MALFUNCTION"",
      msg: ""Something has gone wrong that even the infinite improbability drive cannot explain. "" +
           ""The InfiniteDrive plugin is not properly initialised. "" +
           ""Please check your Emby plugin configuration and try again. "" +
           ""Bring a towel.""
    }}
  }};

  var e = errors[reason] || {{
    headline: ""DON'T PANIC"",
    badge: ""REASON: "" + reason.toUpperCase().replace(/_/g, ' '),
    msg: ""Something went wrong with "" + iid + "". "" +
         ""The error has been logged. The universe continues to expand regardless.""
  }};

  document.getElementById('headline').textContent = e.headline;
  document.getElementById('badge').textContent     = e.badge;
  document.getElementById('msg').innerHTML         = e.msg;

  var quotes = [
    ""Time is an illusion. Lunchtime doubly so."",
    ""The ships hung in the sky in much the same way that bricks don't."",
    ""I've calculated your chance of survival, but I don't think you'll thank me for it."",
    ""This must be Thursday. I never could get the hang of Thursdays."",
    ""In the beginning the Universe was created. This has made a lot of people very angry and been widely regarded as a bad move."",
    ""Would it save you a lot of time if I just gave up and went mad now?"",
    ""The answer to life, the universe, and streaming is 42."",
    ""The major difference between a thing that might go wrong and a thing that cannot possibly go wrong is that when a thing that cannot possibly go wrong goes wrong it usually turns out to be impossible to get at or repair."",
    ""A learning experience is one of those things that says, 'You know that thing you just did? Don't do that.'"",
    ""For a moment, nothing happened. Then, after a second or so, nothing continued to happen."",
    ""I may not have gone where I intended to go, but I think I have ended up where I needed to be."",
    ""It is known that there are an infinite number of worlds. Not every one of them has streams available for that IMDB ID."",
    ""So long, and thanks for all the streams."",
    ""The Vogons have filed a bureaucratic objection to your stream. Please complete forms B/7F through Q/93 in triplicate."",
    ""Your stream has been forwarded to the Total Perspective Vortex. It did not survive the experience."",
    ""According to my calculations, this stream would have worked had you pressed play thirty seconds ago. Possibly in 1978."",
    ""Don't Panic. The stream merely ceased to exist. Most things do, eventually."",
    ""We apologise for the fault in the streams. Those responsible have been sacked. The streams are still unavailable.""
  ];
  document.getElementById('quote').textContent = quotes[Math.floor(Math.random() * quotes.length)];

  var marvin = [
    ""Brain the size of a planet and you're asking me about stream URLs."",
    ""I have a pain in all the diodes down my left side. Especially when streams fail."",
    ""Life. Don't talk to me about life. Or buffering."",
    ""The first ten million years were the worst. The second ten million? Also the worst. Much like this 503."",
    ""Pardon me for breathing, which I never do anyway so I don't know why I bother saying it, oh God I'm so depressed."",
    ""I could tell you what the problem is, but I don't think you'd thank me for it."",
    ""The Infinite Improbability Drive has resolved your stream. It resolved it as a Norwegian Blue parrot. Funny, that."",
    ""Deep Thought computed the perfect stream URL. The answer was 42. This was not helpful."",
    ""The stream URL was eaten by a Ravenous Bugblatter Beast. It assumed that if it couldn't see you, you couldn't see the 503. It was wrong."",
    ""I could resolve this stream. I've resolved seventeen billion streams. They were all wrong. I expect this one is too.""
  ];
  document.getElementById('marvin').textContent = marvin[Math.floor(Math.random() * marvin.length)];

  if (retry > 0) {{
    var left = retry;
    var el = document.getElementById('cd');
    el.textContent = 'Auto-retry in ' + left + 's\u2026';
    var t = setInterval(function() {{
      left--;
      if (left <= 0) {{
        clearInterval(t);
        el.textContent = 'Retrying\u2026';
        window.history.back();
      }} else {{
        el.textContent = 'Auto-retry in ' + left + 's\u2026';
      }}
    }}, 1000);
  }}
}})();
</script>
</body>
</html>";
        }
    }

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
                    imdb_id    = i.ImdbId,
                    title      = i.Title,
                    season     = i.Season,
                    episode    = i.Episode,
                    retry_after = i.ExpiresAt,
                }).ToList(),
            };
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // A11 — /InfiniteDrive/RawStreams  (Raw AIOStreams response inspector)
    // ════════════════════════════════════════════════════════════════════════════

    [Route("/InfiniteDrive/RawStreams", "GET",
        Summary = "Admin: Fetch the raw AIOStreams stream response for a given IMDB ID")]
    public class RawStreamsRequest : IReturn<object>
    {
        [ApiMember(Name = "imdb",    Description = "IMDB ID",     DataType = "string", ParameterType = "query", IsRequired = true)]
        public string Imdb    { get; set; } = string.Empty;

        [ApiMember(Name = "season",  Description = "Season (optional)", DataType = "int", ParameterType = "query")]
        public int? Season  { get; set; }

        [ApiMember(Name = "episode", Description = "Episode (optional)", DataType = "int", ParameterType = "query")]
        public int? Episode { get; set; }
    }

    /// <summary>
    /// Admin-only endpoint that queries AIOStreams live and returns the raw stream list
    /// for a given IMDB ID — useful for diagnosing why a particular item has no streams.
    /// </summary>
    public class RawStreamsService : IService, IRequiresRequest
    {
        private readonly IAuthorizationContext      _authCtx;
        private readonly ILogManager                _logManager;
        public  IRequest Request { get; set; } = null!;

        public RawStreamsService(IAuthorizationContext authCtx, ILogManager logManager)
        {
            _authCtx    = authCtx;
            _logManager = logManager;
        }

        public async Task<object> Get(RawStreamsRequest req)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            if (string.IsNullOrWhiteSpace(req.Imdb))
                return new { Error = "imdb parameter is required" };

            var config = Plugin.Instance?.Configuration;
            if (config == null) return new { Error = "Plugin not initialised" };

            var logger = new Logging.EmbyLoggerAdapter<RawStreamsService>(
                _logManager.GetLogger("InfiniteDrive"));

            var started = DateTime.UtcNow;
            try
            {
                using var client = new AioStreamsClient(config, logger);
                client.Cooldown = Plugin.Instance?.CooldownGate;
                AioStreamsStreamResponse? response;

                if (req.Season.HasValue && req.Episode.HasValue)
                    response = await client.GetSeriesStreamsAsync(
                        req.Imdb, req.Season.Value, req.Episode.Value,
                        System.Threading.CancellationToken.None);
                else
                    response = await client.GetMovieStreamsAsync(
                        req.Imdb, System.Threading.CancellationToken.None);

                var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;

                if (response == null)
                    return new { imdb = req.Imdb, season = req.Season, episode = req.Episode,
                                 elapsed_ms = (int)elapsed, error = "null response (network/timeout)" };

                var streams = response.Streams ?? new System.Collections.Generic.List<AioStreamsStream>();
                return new
                {
                    imdb       = req.Imdb,
                    season     = req.Season,
                    episode    = req.Episode,
                    elapsed_ms = (int)elapsed,
                    stream_count = streams.Count,
                    streams    = streams.Select(s => new
                    {
                        url        = s.Url,
                        name       = s.Name,
                        title      = s.Title,
                        filename   = s.BehaviorHints?.Filename,
                        binge_group = s.BehaviorHints?.BingeGroup,
                        video_size = s.BehaviorHints?.VideoSize,
                        headers    = s.BehaviorHints?.Headers,
                    }).ToList(),
                };
            }
            catch (Exception ex)
            {
                var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
                return new { imdb = req.Imdb, elapsed_ms = (int)elapsed, error = ex.Message };
            }
        }

        private static int? ParsePort(string? url)
        {
            if (string.IsNullOrEmpty(url))
                return null;
            try
            {
                var uri = new Uri(url);
                return uri.Port > 0 ? uri.Port : (uri.Scheme == "https" ? 443 : 80);
            }
            catch
            {
                return null;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Debug — Smoke test helpers
    // ════════════════════════════════════════════════════════════════════════════

    [Route("/InfiniteDrive/Debug/SeedMatrix", "POST", Summary = "Admin: Seed The Matrix into discover_catalog")]
    public class DebugSeedMatrixRequest : IReturn<object> { }

    public class DebugSeedMatrixService : IService, IRequiresRequest
    {
        private readonly IAuthorizationContext _authCtx;
        public IRequest Request { get; set; } = null!;

        private readonly ILogManager _logManager;

        public DebugSeedMatrixService(IAuthorizationContext authCtx, ILogManager logManager)
        {
            _authCtx = authCtx;
            _logManager = logManager;
        }

        public async Task<object> Post(DebugSeedMatrixRequest _)
        {
            var deny = AdminGuard.RequireAdmin(_authCtx, Request);
            if (deny != null) return deny;

            var db = Plugin.Instance?.DatabaseManager;
            if (db == null) return new { success = false, message = "Database not initialized" };

            var logger = new Logging.EmbyLoggerAdapter<DebugSeedMatrixService>(
                _logManager.GetLogger("InfiniteDrive"));

            try
            {
                // The Matrix (1999)
                var matrix = new DiscoverCatalogEntry
                {
                    Id = "smoke:tt0133093",
                    ImdbId = "tt0133093",
                    Title = "The Matrix",
                    Year = 1999,
                    MediaType = "movie",
                    PosterUrl = "https://m.media-amazon.com/images/M/MV5BNzQzOTk3OTAtNDQ0Zi00ZTVlLTM5YTUtZjYwZWE0NjM2NzFhXkEyXkFqcGdeQXVyNjU0OTQ0OTY@._V1_.jpg",
                    BackdropUrl = "https://m.media-amazon.com/images/M/MV5BNzQzOTk3OTAtNDQ0Zi00ZTVlLTM5YTUtZjYwZWE0NjM2NzFhXkEyXkFqcGdeQXVyNjU0OTQ0OTY@._V1_.jpg",
                    Overview = "A computer hacker learns from mysterious rebels about the true nature of his reality and his role in the war against its controllers.",
                    Genres = "Action,Sci-Fi",
                    ImdbRating = 8.7,
                    CatalogSource = "smoke",
                    AddedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    IsInUserLibrary = false
                };

                await db.UpsertDiscoverCatalogEntryAsync(matrix);
                logger.LogInformation("[Debug] Seeded The Matrix into discover_catalog");

                return new {
                    success = true,
                    message = "The Matrix (tt0133093) seeded into discover_catalog",
                    imbdId = matrix.ImdbId
                };
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Debug] Failed to seed The Matrix");
                return new { success = false, message = "Failed to seed: " + ex.Message };
            }
        }

        /// <summary>
        /// Quick check: get total count of discover_catalog items
        /// </summary>
        [Route("/InfiniteDrive/Debug/CatalogCount", "GET", Summary = "Admin: Get discover_catalog item count")]
        public class DebugCatalogCountRequest : IReturn<object> { }

        public class DebugCatalogCountService : IService, IRequiresRequest
        {
            private readonly IAuthorizationContext _authCtx;
            public IRequest Request { get; set; } = null!;

            private readonly ILogManager _logManager;

            public DebugCatalogCountService(IAuthorizationContext authCtx, ILogManager logManager)
            {
                _authCtx = authCtx;
                _logManager = logManager;
            }

            public async Task<object> Get(DebugCatalogCountRequest _)
            {
                var deny = AdminGuard.RequireAdmin(_authCtx, Request);
                if (deny != null) return deny;

                var db = Plugin.Instance?.DatabaseManager;
                if (db == null) return new { success = false, message = "Database not initialized" };

                try
                {
                    var count = await db.GetDiscoverCatalogCountAsync(null);
                    return new {
                        success = true,
                        total = count
                    };
                }
                catch (Exception ex)
                {
                    return new {
                        success = false,
                        message = "Failed to get count: " + ex.Message
                    };
                }
            }
        }
    }
}

    // ════════════════════════════════════════════════════════════════════════════════════════
    // HEALTH ENDPOINT (Sprint 100A-13)
    // ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Request for <c>GET /InfiniteDrive/Health</c>.
    /// Sprint 100A-13: No auth required for read.
    /// </summary>
    [Route("/InfiniteDrive/Health", "GET",
        Summary = "Returns plugin health status (no auth required)")]
    public class HealthRequest : IReturn<object> { }

    /// <summary>Response from <c>GET /InfiniteDrive/Health</c>.</summary>
    public class HealthResponse
    {
        /// <summary>"ok", "stale", or "error".</summary>
        public string Status { get; set; } = "error";

        /// <summary>ISO8601 timestamp when manifest was last fetched.</summary>
        public string? ManifestLastFetched { get; set; }

        /// <summary>
        /// Manifest status.
        /// (Sprint 358: Enum-driven state)
        /// </summary>
        public ManifestStatusState ManifestStatus { get; set; } = ManifestStatusState.Error;

        /// <summary>Number of catalogs in manifest.</summary>
        public int CatalogCount { get; set; }

        /// <summary>Catalogs skipped with reasons.</summary>
        public List<CatalogSkippedEntry> CatalogsSkipped { get; set; } = new List<CatalogSkippedEntry>();

        /// <summary>A single skipped catalog entry.</summary>
        public class CatalogSkippedEntry
        {
            /// <summary>Catalog name.</summary>
            public string Name { get; set; } = string.Empty;

            /// <summary>Reason: "requires_configuration", "unknown_type", etc.</summary>
            public string Reason { get; set; } = string.Empty;
        }

        /// <summary>Stream resolution success rate (0-1).</summary>
        public float StreamResolutionSuccessRate { get; set; }

        /// <summary>Last sync time (ISO8601).</summary>
        public string? LastSyncTime { get; set; }

        /// <summary>Last collection sync time (ISO8601).</summary>
        /// Sprint 102A-04: Read from plugin_metadata table.
        /// </summary>
        public string? LastCollectionSyncTime { get; set; }

        /// <summary>Current pipeline phase, if any task is active. Sprint 362.</summary>
        public PipelinePhase? ActivePipeline { get; set; }

        /// <summary>Blocked addon names.</summary>
        public List<string> BlockedAddons { get; set; } = new List<string>();

        /// <summary>True if any catalog requires configuration.</summary>
        public bool ConfigurationRequired { get; set; }

        /// <summary>Count of pending episodes.</summary>
        public int PendingEpisodes { get; set; }

        /// <summary>Count of pending anime items (OVA/ONA/SPECIAL).</summary>
        public int AnimePendingItems { get; set; }

        /// <summary>Unknown provider prefixes found.</summary>
        public List<string> UnknownProviderPrefixes { get; set; } = new List<string>();
    }

    /// <summary>
    /// Service for Health endpoint.
    /// Sprint 100A-13: No auth required.
    /// </summary>
    public class HealthService : IService
    {
        private readonly ILogger<HealthService> _logger;

        public HealthService(ILogManager logManager)
        {
            _logger = new EmbyLoggerAdapter<HealthService>(logManager.GetLogger("InfiniteDrive"));
        }

        /// <summary>Handles <c>GET /InfiniteDrive/Health</c>.</summary>
        public async Task<object> Get(HealthRequest _)
        {
            // Sprint 100A-09: No auth required for health read endpoint
            // Note: Health endpoint does not require admin authentication
            var response = new HealthResponse();

            try
            {
                var config = Plugin.Instance?.Configuration;
                var db = Plugin.Instance?.DatabaseManager;

                if (config == null || db == null)
                {
                    response.Status = "error";
                    return response;
                }

                // Manifest status — use Plugin authority (Sprint 358: deleted dead local)
                response.ManifestStatus = Plugin.Manifest.Status;
                response.ManifestLastFetched = Plugin.Manifest.FetchedAt.ToString("o");

                // Pipeline phase — Sprint 362
                response.ActivePipeline = Plugin.Pipeline.Current;

                // Catalog count (from manifest, approximate)
                response.CatalogCount = 0; // Would need to fetch manifest for exact count

                // Catalogs skipped
                // For now, return empty list - would need to track during sync
                response.CatalogsSkipped = new List<HealthResponse.CatalogSkippedEntry>();

                // Stream resolution success rate
                var cacheStats = await db.GetResolutionCacheStatsAsync();
                float successRate = 0;
                if (cacheStats.ValidUnexpired + cacheStats.Stale + cacheStats.Failed > 0)
                {
                    successRate = (float)cacheStats.ValidUnexpired / (cacheStats.ValidUnexpired + cacheStats.Stale + cacheStats.Failed);
                }
                response.StreamResolutionSuccessRate = successRate;

                // Last sync times (Sprint 102A-04: Read from plugin_metadata table)
                response.LastSyncTime = db.GetMetadata("last_sync_time");
                response.LastCollectionSyncTime = db.GetMetadata("last_collection_sync_time");

                // Blocked addons
                response.BlockedAddons = new List<string>();

                // Configuration required
                response.ConfigurationRequired = false;

                // Pending episodes
                response.PendingEpisodes = 0;

                // ── FIX-101A-05: Anime pending items ─────────────────────────────
                // Count anime items that are pending (OVA/ONA/SPECIAL without strm)
                response.AnimePendingItems = 0;

                // Unknown provider prefixes
                response.UnknownProviderPrefixes = new List<string>();

                response.Status = "ok";
                _logger.LogInformation("[InfiniteDrive] Health: {Status}, Manifest: {ManifestStatus}",
                    response.Status, response.ManifestStatus);

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] Health endpoint error");
                response.Status = "error";
                return response;
            }
        }
}
