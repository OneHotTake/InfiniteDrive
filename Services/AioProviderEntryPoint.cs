using System.Collections.Generic;
using System.Text.Json;
using InfiniteDrive.Logging;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Reports provider readiness at server startup.
    ///
    /// All InfiniteDrive providers, including AioMediaSourceProvider, are
    /// discovered by Emby's assembly scan. Do not call IMediaSourceManager.AddParts
    /// here: on Emby 4.10 that method replaces the complete provider set and would
    /// remove Live TV and channel providers registered by the server.
    /// </summary>
    public class AioProviderEntryPoint : IServerEntryPoint
    {
        private readonly ILogger<AioProviderEntryPoint> _logger;

        public AioProviderEntryPoint(ILogManager logManager)
        {
            _logger = new EmbyLoggerAdapter<AioProviderEntryPoint>(logManager.GetLogger("InfiniteDrive"));
        }

        public void Run()
        {
            _logger.LogInformation("[InfiniteDrive] AioMediaSourceProvider available through Emby provider discovery");

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
