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
}
