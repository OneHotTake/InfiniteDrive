using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Logging;
using EmbyStreams.Services;
using Microsoft.Extensions.Logging;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Prioritized metadata chain service.
    /// Sprint 100C-03: Metadata chain.
    /// Prioritizes Cinemeta over AIOStreams, uses AIOMetadata fallback,
    /// records collection membership from meta responses.
    /// </summary>
    public class MetadataChainService
    {
        private readonly ILogger _logger;
        private readonly DatabaseManager _db;
        private readonly HttpClient _httpClient;

        private const string CinemetaBaseUrl = "https://v3-cinemeta.strem.io";

        public MetadataChainService(ILogManager logManager, DatabaseManager db)
        {
            _logger = new EmbyLoggerAdapter<MetadataChainService>(logManager.GetLogger("EmbyStreams"));
            _db = db;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        /// <summary>
        /// Fetches metadata with the priority chain.
        /// 1. Try Cinemeta (richer metadata: plot, genres, cast, images)
        /// 2. Try AIOMetadata (if Cinemeta disabled)
        /// 3. Try AIOStreams (if both above disabled)
        /// 4. Record collection membership if found
        /// Sprint 100C-03: Metadata chain.
        /// </summary>
        public async Task<JsonElement?> FetchMetadataAsync(
            string itemType,
            string itemId,
            string? aioManifestUrl,
            string? aioMetadataUrl,
            bool enableCinemeta,
            CancellationToken cancellationToken = default)
        {
            JsonElement? meta = null;

            // 1. Try Cinemeta (richer metadata)
            if (enableCinemeta)
            {
                try
                {
                    _logger.LogDebug(
                        "[MetadataChainService] Trying Cinemeta for {Type} {Id}",
                        itemType, itemId);

                    var cinemetaClient = new AioStreamsClient(CinemetaBaseUrl, string.Empty, string.Empty, _logger);
                    meta = await cinemetaClient.GetMetaAsync(itemType, itemId, cancellationToken);

                    if (meta != null && meta.Value.ValueKind == JsonValueKind.Object)
                    {
                        _logger.LogDebug(
                            "[MetadataChainService] Got metadata from Cinemeta for {Type} {Id}",
                            itemType, itemId);

                        // Record collection membership from Cinemeta
                        RecordCollectionMembership(meta.Value, itemId, "cinemeta");
                        return meta;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "[MetadataChainService] Cinemeta fetch failed for {Type} {Id}",
                        itemType, itemId);
                }
            }

            // 2. Try AIOMetadata (if configured)
            if (meta == null && !string.IsNullOrEmpty(aioMetadataUrl))
            {
                try
                {
                    _logger.LogDebug(
                        "[MetadataChainService] Trying AIOMetadata for {Type} {Id}",
                        itemType, itemId);

                    var aioMetaClient = new AioStreamsClient(aioMetadataUrl, string.Empty, string.Empty, _logger);
                    meta = await aioMetaClient.GetMetaAsync(itemType, itemId, cancellationToken);

                    if (meta != null && meta.Value.ValueKind == JsonValueKind.Object)
                    {
                        _logger.LogDebug(
                            "[MetadataChainService] Got metadata from AIOMetadata for {Type} {Id}",
                            itemType, itemId);

                        // Record collection membership from AIOMetadata
                        RecordCollectionMembership(meta.Value, itemId, "aiometadata");
                        return meta;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "[MetadataChainService] AIOMetadata fetch failed for {Type} {Id}",
                        itemType, itemId);
                }
            }

            // 3. Try AIOStreams (primary manifest)
            if (meta == null && !string.IsNullOrEmpty(aioManifestUrl))
            {
                try
                {
                    _logger.LogDebug(
                        "[MetadataChainService] Trying AIOStreams for {Type} {Id}",
                        itemType, itemId);

                    var aioClient = new AioStreamsClient(aioManifestUrl, string.Empty, string.Empty, _logger);
                    meta = await aioClient.GetMetaAsync(itemType, itemId, cancellationToken);

                    if (meta != null && meta.Value.ValueKind == JsonValueKind.Object)
                    {
                        _logger.LogDebug(
                            "[MetadataChainService] Got metadata from AIOStreams for {Type} {Id}",
                            itemType, itemId);

                        // Record collection membership from AIOStreams
                        RecordCollectionMembership(meta.Value, itemId, "aiostreams");
                        return meta;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "[MetadataChainService] AIOStreams fetch failed for {Type} {Id}",
                        itemType, itemId);
                }
            }

            // 4. Fallback: log warning
            if (meta == null)
            {
                _logger.LogWarning(
                    "[MetadataChainService] All metadata sources failed for {Type} {Id}",
                    itemType, itemId);
            }

            return meta;
        }

        /// <summary>
        /// Records collection membership from metadata response.
        /// Sprint 100C-01: Collection membership recording.
        /// Sprint 100C-03: Metadata chain integration.
        /// </summary>
        private void RecordCollectionMembership(JsonElement meta, string embyItemId, string source)
        {
            try
            {
                // Check for collection field
                string? collectionName = null;

                if (meta.TryGetProperty("collection", out var collectionProp)
                    && collectionProp.ValueKind == JsonValueKind.String)
                {
                    collectionName = collectionProp.GetString();
                }
                // Check for belongsToCollection field
                else if (meta.TryGetProperty("belongsToCollection", out var belongsProp)
                    && belongsProp.ValueKind == JsonValueKind.String)
                {
                    collectionName = belongsProp.GetString();
                }

                if (!string.IsNullOrEmpty(collectionName) && !string.IsNullOrEmpty(embyItemId))
                {
                    _logger.LogDebug(
                        "[MetadataChainService] Recording collection membership: {Collection} <- {Item} from {Source}",
                        collectionName, embyItemId, source);

                    // Record in database
                    var _ = _db.UpsertCollectionMembershipAsync(
                        collectionName,
                        embyItemId,
                        source);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "[MetadataChainService] Failed to record collection membership: {Ex}",
                    ex.Message);
            }
        }

        /// <summary>
        /// Clears collection memberships for a source.
        /// Used when re-syncing catalogs.
        /// Sprint 100C-03: Metadata chain integration.
        /// </summary>
        public async Task ClearCollectionMembershipsForSourceAsync(
            string source,
            CancellationToken cancellationToken = default)
        {
            await _db.ClearCollectionMembershipsBySourceAsync(source, cancellationToken);
            _logger.LogInformation(
                "[MetadataChainService] Cleared collection memberships for source: {Source}",
                source);
        }
    }
}
