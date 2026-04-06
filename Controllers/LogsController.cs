using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Models;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Controllers
{
    /// <summary>
    /// API endpoints for log retrieval.
    /// </summary>
    [Route("embystreams/logs")]
    public class LogsController : IService, IRequiresRequest
    {
        private readonly DatabaseManager _db;
        private readonly ILogger<LogsController> _logger;

        public LogsController(DatabaseManager db, ILogger<LogsController> logger)
        {
            _db = db;
            _logger = logger;
        }

        public IRequest Request { get; set; } = null!;

        /// <summary>
        /// Gets pipeline logs with optional filters.
        /// GET /embystreams/logs/pipeline
        /// </summary>
        [Route("pipeline")]
        public async Task<List<PipelineLogEntry>> Get(PipelineLogsRequest request, CancellationToken ct)
        {
            _logger.LogDebug("[LogsController] Get pipeline logs request");
            return await _db.GetPipelineLogsAsync(
                request.PrimaryId,
                request.PrimaryIdType,
                request.MediaType,
                request.Trigger,
                request.Limit,
                ct);
        }

        /// <summary>
        /// Gets resolution logs with optional filters.
        /// GET /embystreams/logs/resolution
        /// </summary>
        [Route("resolution")]
        public async Task<List<ResolutionLogEntry>> Get(ResolutionLogsRequest request, CancellationToken ct)
        {
            _logger.LogDebug("[LogsController] Get resolution logs request");
            return await _db.GetResolutionLogsAsync(
                request.PrimaryId,
                request.PrimaryIdType,
                request.MediaType,
                request.Limit,
                ct);
        }

        /// <summary>
        /// Gets recent logs with optional level filter.
        /// GET /embystreams/logs/recent
        /// </summary>
        [Route("recent")]
        public async Task<List<RecentLogEntry>> Get(RecentLogsRequest request, CancellationToken ct)
        {
            _logger.LogDebug("[LogsController] Get recent logs request: Level={Level}", request.Level);
            return await _db.GetRecentLogsAsync(
                request.Level,
                request.Limit,
                ct);
        }
    }

    /// <summary>
    /// Request DTO for pipeline logs.
    /// </summary>
    public class PipelineLogsRequest
    {
        public string? PrimaryId { get; set; }
        public string? PrimaryIdType { get; set; }
        public string? MediaType { get; set; }
        public string? Trigger { get; set; }
        public int Limit { get; set; } = 100;
    }

    /// <summary>
    /// Request DTO for resolution logs.
    /// </summary>
    public class ResolutionLogsRequest
    {
        public string? PrimaryId { get; set; }
        public string? PrimaryIdType { get; set; }
        public string? MediaType { get; set; }
        public int Limit { get; set; } = 100;
    }

    /// <summary>
    /// Request DTO for recent logs.
    /// </summary>
    public class RecentLogsRequest
    {
        public string? Level { get; set; }
        public int Limit { get; set; } = 100;
    }
}
