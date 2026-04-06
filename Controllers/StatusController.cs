using MediaBrowser.Model.Services;

namespace EmbyStreams.Controllers
{
    /// <summary>
    /// API endpoint for plugin status.
    /// </summary>
    [Route("embystreams/status")]
    public class StatusController : IService, IRequiresRequest
    {
        public IRequest Request { get; set; } = null!;

        /// <summary>
        /// Gets plugin status.
        /// GET /embystreams/status
        /// </summary>
        public StatusResponse Get()
        {
            return new StatusResponse
            {
                Version = EmbyStreams.Plugin.Instance?.Version.ToString() ?? "unknown",
                SchemaVersion = 3, // v3.3 schema
                LastSyncAt = GetLastSyncTime(),
                DatabasePath = GetDatabasePath(),
                PluginStatus = "ok"
            };
        }

        private string? GetLastSyncTime()
        {
            // TODO: Get actual last sync time from database
            return null;
        }

        private string? GetDatabasePath()
        {
            // TODO: Get actual database path
            return null;
        }
    }

    /// <summary>
    /// Status response DTO.
    /// </summary>
    public class StatusResponse
    {
        public string Version { get; set; } = string.Empty;
        public int SchemaVersion { get; set; }
        public string? LastSyncAt { get; set; }
        public string? DatabasePath { get; set; }
        public string PluginStatus { get; set; } = string.Empty;
    }
}
