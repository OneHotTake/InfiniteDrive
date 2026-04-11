using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;

namespace InfiniteDrive.Tasks
{
    /// <summary>
    /// Syncs Emby BoxSet collections from provider metadata.
    /// Sprint 100C-02: Collection sync task (new).
    /// Runs after DoctorTask, uses Emby REST API to create/update BoxSets.
    /// Only operates on BoxSets tagged "InfiniteDrive:managed".
    /// </summary>
    public class CollectionSyncTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly DatabaseManager _db;
        private readonly ILibraryManager _libraryManager;

        public CollectionSyncTask(
            ILogManager logManager,
            DatabaseManager db,
            ILibraryManager libraryManager)
        {
            _logger = new EmbyLoggerAdapter<CollectionSyncTask>(logManager.GetLogger("InfiniteDrive"));
            _db = db;
            _libraryManager = libraryManager;
        }

        /// <summary>
        /// Task key for manual trigger via /InfiniteDrive/Trigger.
        /// Sprint 100C-02: Task key for triggering.
        /// </summary>
        public static string TaskKey => "collection_sync";

        public string Name => "InfiniteDrive: Collection Sync";
        public string Key => "CollectionSyncTask";
        public string Description => "Syncs Emby BoxSets from provider collection metadata";
        public string Category => "InfiniteDrive";

        /// <summary>
        /// Executes collection sync.
        /// 1. Fetches all collections from database
        /// 2. For each collection: creates/updates Emby BoxSet
        /// 3. Adds/removes items to/from BoxSet
        /// 4. Tags BoxSet as "InfiniteDrive:managed"
        /// Sprint 100C-02: Collection sync task.
        /// </summary>
        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null || string.IsNullOrWhiteSpace(config.EmbyBaseUrl))
                {
                    _logger.LogWarning("[CollectionSyncTask] Cannot run - EmbyBaseUrl not configured");
                    return;
                }

                _logger.LogInformation("[CollectionSyncTask] Starting collection sync");

                // Fetch all collections from database
                var collections = await _db.GetAllCollectionsAsync(cancellationToken);
                _logger.LogInformation("[CollectionSyncTask] Found {Count} collections to sync", collections.Count);

                if (collections.Count == 0)
                {
                    _logger.LogInformation("[CollectionSyncTask] No collections to sync");
                    return;
                }

                int totalCollections = collections.Count;
                int currentCollection = 0;
                int totalAdded = 0;
                int totalRemoved = 0;
                int totalErrors = 0;

                // Create HTTP client for Emby REST API calls
                using var httpClient = CreateEmbyHttpClient(config.EmbyBaseUrl, config.PluginSecret);

                foreach (var kvp in collections)
                {
                    currentCollection++;
                    var collectionName = kvp.Key;
                    var itemIds = await _db.GetCollectionMembersAsync(collectionName, cancellationToken);

                    // Report progress
                    progress.Report(currentCollection * 100.0 / totalCollections);

                    _logger.LogInformation(
                        "[CollectionSyncTask] Processing collection '{Name}' ({Count} items)",
                        collectionName, itemIds.Count);

                    try
                    {
                        // 1. Search for existing BoxSet
                        var boxSetId = await FindOrCreateBoxSetAsync(
                            httpClient, collectionName, cancellationToken);

                        if (boxSetId == null)
                        {
                            _logger.LogWarning(
                                "[CollectionSyncTask] Failed to find/create BoxSet '{Name}'",
                                collectionName);
                            totalErrors++;
                            continue;
                        }

                        // 2. Ensure BoxSet is tagged as managed
                        await TagBoxSetAsManagedAsync(httpClient, boxSetId, cancellationToken);

                        // 3. Get current members in Emby BoxSet
                        var currentMembers = await GetBoxSetMembersAsync(
                            httpClient, boxSetId, cancellationToken) ?? new List<string>();

                        // 4. Calculate changes
                        var toAdd = itemIds.Except(currentMembers, StringComparer.OrdinalIgnoreCase).ToList();
                        var toRemove = currentMembers.Except(itemIds, StringComparer.OrdinalIgnoreCase).ToList();

                        // 5. Add new items to BoxSet
                        if (toAdd.Count > 0)
                        {
                            await AddItemsToBoxSetAsync(
                                httpClient, boxSetId, toAdd, cancellationToken);
                            totalAdded += toAdd.Count;
                            _logger.LogInformation(
                                "[CollectionSyncTask] Collection '{Name}': {Count} items added",
                                collectionName, toAdd.Count);
                        }

                        // 6. Remove orphaned items from BoxSet
                        if (toRemove.Count > 0)
                        {
                            await RemoveItemsFromBoxSetAsync(
                                httpClient, boxSetId, toRemove, cancellationToken);
                            totalRemoved += toRemove.Count;
                            _logger.LogInformation(
                                "[CollectionSyncTask] Collection '{Name}': {Count} items removed",
                                collectionName, toRemove.Count);
                        }

                        _logger.LogInformation(
                            "[CollectionSyncTask] Collection '{Name}': {Added} added, {Removed} removed, {Total} total members",
                            collectionName, toAdd.Count, toRemove.Count, itemIds.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "[CollectionSyncTask] Error processing collection '{Name}'",
                            collectionName);
                        totalErrors++;
                    }
                }

                _logger.LogInformation(
                    "[CollectionSyncTask] Collection sync complete — {Collections} processed, {Added} added, {Removed} removed, {Errors} errors",
                    totalCollections, totalAdded, totalRemoved, totalErrors);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CollectionSyncTask] Collection sync failed");
            }
            finally
            {
                // Sprint 102A-03: Persist last collection sync time
                try
                {
                    await _db.PersistMetadataAsync(
                        "last_collection_sync_time",
                        DateTimeOffset.UtcNow.ToString("o"),
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[CollectionSyncTask] Failed to persist last_collection_sync_time");
                }
            }
        }

        /// <summary>
        /// Creates HttpClient for Emby REST API calls.
        /// </summary>
        private HttpClient CreateEmbyHttpClient(string baseUrl, string apiKey)
        {
            var client = new HttpClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Add("X-MediaBrowser-Token", apiKey);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.Timeout = TimeSpan.FromMinutes(5);
            return client;
        }

        /// <summary>
        /// Searches for an existing BoxSet by name, creates one if not found.
        /// Only operates on BoxSets tagged "InfiniteDrive:managed".
        /// Sprint 100C-02: Find or create BoxSet.
        /// </summary>
        private async Task<string?> FindOrCreateBoxSetAsync(
            HttpClient httpClient,
            string collectionName,
            CancellationToken cancellationToken)
        {
            // 1. Search for existing BoxSet
            var searchUrl = $"Items?SearchTerm={Uri.EscapeDataString(collectionName)}&IncludeItemTypes=BoxSet&Recursive=true";
            var response = await httpClient.GetAsync(searchUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[CollectionSyncTask] BoxSet search failed: {StatusCode}",
                    response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = JsonDocument.Parse(json);

            // Check for existing managed BoxSet
            var items = doc.RootElement.GetProperty("Items").EnumerateArray();
            JsonElement? existingBoxSet = null;
            foreach (var item in items)
            {
                if (!item.TryGetProperty("Name", out var nameProp))
                    continue;
                var name = nameProp.GetString();
                if (!string.Equals(name, collectionName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Check for managed tag
                if (item.TryGetProperty("Tags", out var tagsProp)
                    && tagsProp.ValueKind == JsonValueKind.Array)
                {
                    var hasTag = tagsProp.EnumerateArray()
                        .Any(tag => string.Equals(
                            tag.GetString(),
                            "InfiniteDrive:managed",
                            StringComparison.OrdinalIgnoreCase));
                    if (hasTag)
                    {
                        existingBoxSet = item;
                        break;
                    }
                }
            }

            if (existingBoxSet != null)
            {
                var id = existingBoxSet.Value.GetProperty("Id").GetString();
                _logger.LogDebug("[CollectionSyncTask] Found existing BoxSet '{Name}' with ID {Id}", collectionName, id);
                return id;
            }

            // 2. Create new BoxSet
            _logger.LogInformation("[CollectionSyncTask] Creating new BoxSet '{Name}'", collectionName);

            var createUrl = "Collections";
            var createData = new { Name = collectionName };
            var content = new StringContent(
                JsonSerializer.Serialize(createData),
                Encoding.UTF8,
                "application/json");

            var createResponse = await httpClient.PostAsync(createUrl, content, cancellationToken);
            if (!createResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[CollectionSyncTask] BoxSet creation failed: {StatusCode}",
                    createResponse.StatusCode);
                return null;
            }

            var createJson = await createResponse.Content.ReadAsStringAsync(cancellationToken);
            var createdDoc = JsonDocument.Parse(createJson);
            var createdId = createdDoc.RootElement.GetProperty("Id").GetString();

            _logger.LogInformation("[CollectionSyncTask] Created BoxSet '{Name}' with ID {Id}", collectionName, createdId);
            return createdId;
        }

        /// <summary>
        /// Tags a BoxSet as "InfiniteDrive:managed".
        /// Sprint 100C-02: Tag BoxSet as managed.
        /// </summary>
        private async Task TagBoxSetAsManagedAsync(
            HttpClient httpClient,
            string boxSetId,
            CancellationToken cancellationToken)
        {
            var tagsUrl = $"Items/{boxSetId}/Tags";
            var tagData = new[] { "InfiniteDrive:managed" };
            var content = new StringContent(
                JsonSerializer.Serialize(tagData),
                Encoding.UTF8,
                "application/json");

            var response = await httpClient.PostAsync(tagsUrl, content, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("[CollectionSyncTask] Tagged BoxSet {Id} as managed", boxSetId);
            }
            else
            {
                _logger.LogWarning(
                    "[CollectionSyncTask] Failed to tag BoxSet {Id}: {StatusCode}",
                    boxSetId, response.StatusCode);
            }
        }

        /// <summary>
        /// Gets current members of a BoxSet.
        /// Sprint 100C-02: Get BoxSet members.
        /// </summary>
        private async Task<List<string>?> GetBoxSetMembersAsync(
            HttpClient httpClient,
            string boxSetId,
            CancellationToken cancellationToken)
        {
            var url = $"Collections/{boxSetId}/Items?Recursive=true";
            var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[CollectionSyncTask] Failed to get BoxSet members: {StatusCode}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = JsonDocument.Parse(json);

            var members = new List<string>();
            if (doc.RootElement.TryGetProperty("Items", out var itemsProp)
                && itemsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in itemsProp.EnumerateArray())
                {
                    if (item.TryGetProperty("Id", out var idProp))
                    {
                        members.Add(idProp.GetString());
                    }
                }
            }

            return members;
        }

        /// <summary>
        /// Adds items to a BoxSet.
        /// Sprint 100C-02: Add items to BoxSet.
        /// </summary>
        private async Task AddItemsToBoxSetAsync(
            HttpClient httpClient,
            string boxSetId,
            List<string> itemIds,
            CancellationToken cancellationToken)
        {
            if (itemIds.Count == 0)
                return;

            var idsParam = string.Join(",", itemIds);
            var url = $"Collections/{boxSetId}/Items?ids={idsParam}";
            var content = new StringContent(string.Empty);
            await httpClient.PostAsync(url, content, cancellationToken);
        }

        /// <summary>
        /// Removes items from a BoxSet.
        /// Sprint 100C-02: Remove items from BoxSet.
        /// </summary>
        private async Task RemoveItemsFromBoxSetAsync(
            HttpClient httpClient,
            string boxSetId,
            List<string> itemIds,
            CancellationToken cancellationToken)
        {
            if (itemIds.Count == 0)
                return;

            var idsParam = string.Join(",", itemIds);
            var url = $"Collections/{boxSetId}/Items?ids={idsParam}";
            await httpClient.DeleteAsync(url, cancellationToken);
        }

        /// <summary>
        /// Default schedule: daily at 2 AM.
        /// </summary>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerDaily,
                    TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
                }
            };
        }
    }
}
