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
                using var client = AioStreamsClientFactory.Create(logger);
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
}
