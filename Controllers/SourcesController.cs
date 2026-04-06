using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Models;
using EmbyStreams.Services;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;

namespace EmbyStreams.Controllers
{
    /// <summary>
    /// API endpoints for source management.
    /// </summary>
    [Route("embystreams/sources")]
    public class SourcesController : IService, IRequiresRequest
    {
        private readonly SourcesService _service;
        private readonly ILogger<SourcesController> _logger;

        public SourcesController(SourcesService service, ILogger<SourcesController> logger)
        {
            _service = service;
            _logger = logger;
        }

        public IRequest Request { get; set; } = null!;

        /// <summary>
        /// Lists all sources.
        /// GET /embystreams/sources
        /// </summary>
        [Route("")]
        public async Task<List<Source>> Get(CancellationToken ct)
        {
            _logger.LogDebug("[SourcesController] List sources request");
            return await _service.GetSourcesAsync(ct);
        }

        /// <summary>
        /// Creates a new source (Trakt/MdbList only).
        /// POST /embystreams/sources
        /// </summary>
        [Route("")]
        public async Task<Source> Post(CreateSourceRequest request, CancellationToken ct)
        {
            _logger.LogInformation("[SourcesController] Create source request for {Name}", request.Name);

            // CRITICAL: Restrict POST to Trakt/MdbList only
            // AIO and BuiltIn sources are managed internally
            if (request.Type == SourceType.Aio || request.Type == SourceType.BuiltIn)
            {
                _logger.LogWarning("[SourcesController] Cannot create {Type} sources via API", request.Type);
                throw new ArgumentException(
                    $"Cannot create {request.Type} sources. Only Trakt and MdbList sources can be manually added.");
            }

            var source = new Source
            {
                Name = request.Name,
                Url = request.Url,
                Type = request.Type,
                Enabled = true,
                ShowAsCollection = false
            };

            return await _service.CreateSourceAsync(source, ct);
        }

        /// <summary>
        /// Gets a single source by ID.
        /// GET /embystreams/sources/{id}
        /// </summary>
        [Route("{id}")]
        public async Task<Source?> Get(string id, CancellationToken ct)
        {
            _logger.LogDebug("[SourcesController] Get source request for {SourceId}", id);
            return await _service.GetSourceAsync(id, ct);
        }

        /// <summary>
        /// Deletes a source.
        /// DELETE /embystreams/sources/{id}
        /// </summary>
        [Route("{id}")]
        public async Task Delete(string id, CancellationToken ct)
        {
            _logger.LogInformation("[SourcesController] Delete source request for {SourceId}", id);
            await _service.DeleteSourceAsync(id, ct);
        }

        /// <summary>
        /// Enables a source.
        /// POST /embystreams/sources/{id}/enable
        /// </summary>
        [Route("{id}/enable")]
        public async Task EnableSource(string id, CancellationToken ct)
        {
            _logger.LogInformation("[SourcesController] Enable source request for {SourceId}", id);
            await _service.EnableSourceAsync(id, ct);
        }

        /// <summary>
        /// Disables a source.
        /// POST /embystreams/sources/{id}/disable
        /// </summary>
        [Route("{id}/disable")]
        public async Task DisableSource(string id, CancellationToken ct)
        {
            _logger.LogInformation("[SourcesController] Disable source request for {SourceId}", id);
            await _service.DisableSourceAsync(id, ct);
        }

        /// <summary>
        /// Toggles ShowAsCollection flag for a source.
        /// POST /embystreams/sources/{id}/show-as-collection
        /// </summary>
        [Route("{id}/show-as-collection")]
        public async Task ToggleShowAsCollection(string id, ShowAsCollectionRequest request, CancellationToken ct)
        {
            _logger.LogInformation("[SourcesController] Toggle ShowAsCollection for {SourceId}: {Show}", id, request.Show);
            await _service.ToggleShowAsCollectionAsync(id, request.Show, ct);
        }
    }

    /// <summary>
    /// Request DTO for creating a source.
    /// </summary>
    public class CreateSourceRequest
    {
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public SourceType Type { get; set; }
    }

    /// <summary>
    /// Request DTO for toggling ShowAsCollection.
    /// </summary>
    public class ShowAsCollectionRequest
    {
        public bool Show { get; set; }
    }
}
