using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Models;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Manages enabled/disabled sources.
    /// </summary>
    public class SourcesService
    {
        private readonly ILogger<SourcesService> _logger;
        private readonly DatabaseManager _db;

        public SourcesService(ILogger<SourcesService> logger, DatabaseManager db)
        {
            _logger = logger;
            _db = db;
        }

        /// <summary>
        /// Gets all sources.
        /// </summary>
        public Task<List<Source>> GetSourcesAsync(CancellationToken ct = default)
        {
            _logger.LogDebug("[SourcesService] Getting all sources");
            return _db.GetAllSourcesAsync(ct);
        }

        /// <summary>
        /// Creates a new source.
        /// </summary>
        public async Task<Source> CreateSourceAsync(Source source, CancellationToken ct = default)
        {
            _logger.LogInformation("[SourcesService] Creating source {Name}", source.Name);
            await _db.UpsertSourceAsync(source, ct);
            return source;
        }

        /// <summary>
        /// Gets a single source by ID.
        /// </summary>
        public Task<Source?> GetSourceAsync(string sourceId, CancellationToken ct = default)
        {
            _logger.LogDebug("[SourcesService] Getting source {SourceId}", sourceId);
            return _db.GetSourceAsync(sourceId, ct);
        }

        /// <summary>
        /// Deletes a source.
        /// </summary>
        public async Task DeleteSourceAsync(string sourceId, CancellationToken ct = default)
        {
            _logger.LogInformation("[SourcesService] Deleting source {SourceId}", sourceId);
            await _db.DeleteSourceAsync(sourceId, ct);
        }

        /// <summary>
        /// Enables a source.
        /// </summary>
        public Task EnableSourceAsync(string sourceId, CancellationToken ct = default)
        {
            _logger.LogInformation("[SourcesService] Enabling source {SourceId}", sourceId);
            return _db.SetSourceEnabledAsync(sourceId, true, ct);
        }

        /// <summary>
        /// Disables a source.
        /// </summary>
        public Task DisableSourceAsync(string sourceId, CancellationToken ct = default)
        {
            _logger.LogInformation("[SourcesService] Disabling source {SourceId}", sourceId);
            return _db.SetSourceEnabledAsync(sourceId, false, ct);
        }

        /// <summary>
        /// Toggles ShowAsCollection flag for a source.
        /// </summary>
        public Task ToggleShowAsCollectionAsync(string sourceId, bool show, CancellationToken ct = default)
        {
            _logger.LogInformation("[SourcesService] Setting ShowAsCollection for {SourceId} to {Show}", sourceId, show);
            return _db.SetSourceShowAsCollectionAsync(sourceId, show, ct);
        }
    }
}
