using System;
using System.Collections.Generic;
using System.Text.Json;
using InfiniteDrive.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Registers AioMediaSourceProvider with Emby's media source manager.
    /// Called at server startup via IServerEntryPoint.
    ///
    /// AioSeriesMetadataProvider and AioMovieMetadataProvider are
    /// auto-discovered by Emby via assembly scanning — no explicit
    /// registration needed here.
    /// </summary>
    public class AioProviderEntryPoint : IServerEntryPoint
    {
        private readonly ILogger<AioProviderEntryPoint> _logger;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly ILogManager _logManager;

        public AioProviderEntryPoint(
            IMediaSourceManager mediaSourceManager,
            ILogManager logManager)
        {
            _mediaSourceManager = mediaSourceManager;
            _logManager = logManager;
            _logger = new EmbyLoggerAdapter<AioProviderEntryPoint>(logManager.GetLogger("InfiniteDrive"));
        }

        public void Run()
        {
            try
            {
                var provider = new AioMediaSourceProvider(_logManager, _mediaSourceManager);
                _mediaSourceManager.AddParts(new[] { provider });
                _logger.LogInformation("[InfiniteDrive] Registered AioMediaSourceProvider");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] Failed to register AioMediaSourceProvider");
            }

            // Log census state for diagnostics
            try
            {
                var config = Plugin.Instance?.Configuration;
                var census = config?.MetadataIdTypeCensus ?? "{}";
                if (census != "{}")
                {
                    var types = JsonSerializer.Deserialize<Dictionary<string, string>>(census);
                    _logger.LogInformation(
                        "[InfiniteDrive] MetadataProvider ready — {N} ID types in census: {Types}",
                        types?.Count ?? 0,
                        types != null ? string.Join(", ", types.Keys) : "");
                }
                else
                {
                    _logger.LogInformation("[InfiniteDrive] MetadataProvider registered (no census yet — run catalog sync)");
                }
            }
            catch { /* non-fatal */ }
        }

        public void Dispose() { }
    }
}
