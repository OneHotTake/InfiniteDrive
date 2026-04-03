using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Models;
using EmbyStreams.Tasks;
using EmbyStreams.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace EmbyStreams.Services
{
    // ── Request DTO ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Request object for <c>POST /EmbyStreams/Webhook/Sync</c>.
    /// The body is read as raw JSON and auto-detected for one of three formats.
    /// </summary>
    [Route("/EmbyStreams/Webhook/Sync", "POST",
        Summary = "Instant catalog addition or full source re-sync via webhook")]
    public class WebhookSyncRequest : IReturn<object> { }

    // ── Response DTO ─────────────────────────────────────────────────────────────

    /// <summary>JSON response returned by a successful webhook call.</summary>
    public class WebhookResponse
    {
        /// <summary><c>ok</c> or <c>error</c>.</summary>
        public string Status      { get; set; } = "ok";

        /// <summary>Human-readable summary message.</summary>
        public string Message     { get; set; } = string.Empty;

        /// <summary>IMDB ID that was processed, if applicable.</summary>
        public string? Imdb       { get; set; }

        /// <summary>Title of the matched catalog item, if known.</summary>
        public string? Title      { get; set; }

        /// <summary>True if a .strm file was written to disk.</summary>
        public bool StrmWritten   { get; set; }

        /// <summary>True if a Tier 0 resolution job was queued.</summary>
        public bool ResolutionQueued { get; set; }
    }

    // ── Service ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Webhook endpoint that accepts item-addition or full-sync triggers.
    ///
    /// Accepted JSON body formats (auto-detected):
    /// <list type="bullet">
    ///   <item><b>Direct IMDB</b>: <c>{"imdb":"tt15299712"}</c></item>
    ///   <item><b>Source resync</b>: <c>{"source":"trakt"}</c></item>
    ///   <item><b>Jellyseerr / Overseerr</b>:
    ///       <c>{"notification_type":"MEDIA_APPROVED","media":{"imdbId":"tt15299712"}}</c>
    ///   </item>
    ///   <item><b>Radarr</b>:
    ///       <c>{"movie":{"imdbId":"tt15299712"},"eventType":"MovieAdded"}</c>
    ///   </item>
    ///   <item><b>Sonarr</b>:
    ///       <c>{"series":{"imdbId":"tt15299712"},"eventType":"SeriesAdd"}</c>
    ///   </item>
    /// </list>
    ///
    /// When <see cref="PluginConfiguration.WebhookSecret"/> is set the endpoint
    /// validates the caller via one of:
    /// <list type="bullet">
    ///   <item><c>Authorization: Bearer &lt;secret&gt;</c></item>
    ///   <item><c>X-Api-Key: &lt;secret&gt;</c></item>
    ///   <item><c>X-Hub-Signature-256: sha256=&lt;hmac-sha256-of-body&gt;</c> (GitHub-style)</item>
    /// </list>
    ///
    /// For single-item additions the plugin:
    /// <list type="number">
    ///   <item>Upserts the item into <c>catalog_items</c></item>
    ///   <item>Writes a .strm file immediately</item>
    ///   <item>Triggers an Emby library scan</item>
    ///   <item>Queues Tier 0 resolution (immediate stream URL pre-fetch)</item>
    /// </list>
    ///
    /// Target: item visible in Emby library within 10 seconds.
    /// </summary>
    public class WebhookService : IService, IRequiresRequest
    {
        // ── Fields ──────────────────────────────────────────────────────────────

        private readonly ILogger<WebhookService>  _logger;
        private readonly ILibraryManager          _libraryManager;
        private readonly ILogManager              _logManager;

        // ── IRequiresRequest ─────────────────────────────────────────────────────

        /// <inheritdoc/>
        public IRequest Request { get; set; } = null!;

        // ── Constructor ─────────────────────────────────────────────────────────

        /// <summary>
        /// Emby injects <paramref name="libraryManager"/> and
        /// <paramref name="loggerFactory"/> automatically.
        /// </summary>
        public WebhookService(
            ILibraryManager  libraryManager,
            ILogManager      logManager)
        {
            _libraryManager = libraryManager;
            _logManager     = logManager;
            _logger         = new EmbyLoggerAdapter<WebhookService>(logManager.GetLogger("EmbyStreams"));
        }

        // ── IService ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Handles <c>POST /EmbyStreams/Webhook/Sync</c>.
        /// Reads the raw JSON body and delegates to the appropriate handler.
        /// </summary>
        public async Task<object> Post(WebhookSyncRequest _)
        {
            var config = Plugin.Instance?.Configuration;
            var db     = Plugin.Instance?.DatabaseManager;
            if (config == null || db == null)
                return Error("Plugin not initialised");

            // Read raw body
            string body;
            try
            {
                using var reader = new StreamReader(Request.InputStream);
                body = await reader.ReadToEndAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EmbyStreams] Webhook: failed to read body");
                return Error("Failed to read request body");
            }

            if (string.IsNullOrWhiteSpace(body))
                return Error("Empty request body");

            // ── Secret / signature validation ────────────────────────────────────

            if (!string.IsNullOrEmpty(config.WebhookSecret))
            {
                if (!ValidateWebhookSecret(config.WebhookSecret, body))
                {
                    _logger.LogWarning("[EmbyStreams] Webhook: rejected — invalid or missing secret");
                    Request.Response.StatusCode = 401;
                    return Error("Unauthorized — invalid webhook secret");
                }
            }

            JsonElement json;
            try
            {
                using var doc = JsonDocument.Parse(body);
                json = doc.RootElement.Clone();
            }
            catch
            {
                return Error("Invalid JSON body");
            }

            _logger.LogInformation("[EmbyStreams] Webhook received: {Body}", body.Length > 200 ? body.Substring(0, 200) + "…" : body);

            // ── Format detection ─────────────────────────────────────────────────

            // Format B: {"source": "trakt"} — re-sync a source
            var source = GetString(json, "source");
            if (!string.IsNullOrEmpty(source))
                return await HandleSourceResyncAsync(source!);

            // Format C: Jellyseerr notification
            var notifType = GetString(json, "notification_type");
            if (!string.IsNullOrEmpty(notifType))
            {
                var imdbFromJellyseerr = json.TryGetProperty("media", out var media)
                    ? GetString(media, "imdbId") : null;
                if (!string.IsNullOrEmpty(imdbFromJellyseerr))
                    return await HandleSingleItemAsync(imdbFromJellyseerr!, db, config);

                return Error($"Jellyseerr notification received but no media.imdbId found (type={notifType})");
            }

            // Format D: Radarr — {"movie":{"imdbId":"tt..."},"eventType":"MovieAdded"}
            var radarrEventType = GetString(json, "eventType");
            if (!string.IsNullOrEmpty(radarrEventType))
            {
                var imdbFromRadarr = json.TryGetProperty("movie", out var movie)
                    ? GetString(movie, "imdbId") : null;
                if (!string.IsNullOrEmpty(imdbFromRadarr))
                    return await HandleSingleItemAsync(imdbFromRadarr!, db, config);

                var imdbFromSonarr = json.TryGetProperty("series", out var series)
                    ? GetString(series, "imdbId") : null;
                if (!string.IsNullOrEmpty(imdbFromSonarr))
                    return await HandleSingleItemAsync(imdbFromSonarr!, db, config);

                return Error($"Radarr/Sonarr event received but no imdbId found (eventType={radarrEventType})");
            }

            // Format A: {"imdb": "tt..."}
            var imdb = GetString(json, "imdb");
            if (!string.IsNullOrEmpty(imdb))
                return await HandleSingleItemAsync(imdb!, db, config);

            return Error("Unrecognised webhook format — expected {imdb}, {source}, Jellyseerr, Radarr, or Sonarr payload");
        }

        // ── Private: single item ─────────────────────────────────────────────────

        private async Task<object> HandleSingleItemAsync(
            string              imdb,
            Data.DatabaseManager db,
            PluginConfiguration config)
        {
            imdb = imdb.Trim();
            // SEC-2: Full IMDB ID validation — must be "tt" followed by 1–8 digits only.
            // Matches the same rule as PlaybackService.IsValidImdbId.
            if (!IsValidImdbId(imdb))
                return Error($"Invalid IMDB ID: {imdb}. Expected format: tt1234567");

            _logger.LogInformation("[EmbyStreams] Webhook: adding item {Imdb}", imdb);

            // Check if we already have this item
            var existing = await db.GetCatalogItemByImdbIdAsync(imdb);

            CatalogItem item;
            if (existing != null)
            {
                item = existing;
                _logger.LogDebug("[EmbyStreams] Webhook: {Imdb} already in catalog — refreshing", imdb);
            }
            else
            {
                // Resolve metadata from AIOStreams meta endpoint
                item = await FetchOrBuildCatalogItemAsync(imdb, config);
                await db.UpsertCatalogItemAsync(item);
            }

            // Write .strm file immediately
            string? strmPath = null;
            bool strmWritten = false;
            try
            {
                strmPath = await CatalogSyncTask.WriteStrmFileForItemPublicAsync(item, config);
                if (strmPath != null)
                {
                    strmWritten = true;
                    if (existing == null)
                    {
                        item.StrmPath = strmPath;
                        await db.UpdateStrmPathAsync(item.ImdbId, item.Source, strmPath);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EmbyStreams] Webhook: failed to write .strm for {Imdb}", imdb);
            }

            // Trigger Emby scan
            _ = Task.Run(() => TriggerLibraryScan());

            // Queue Tier 0 resolution
            bool resolutionQueued = false;
            try
            {
                if (item.MediaType == "series")
                    await db.QueueForResolutionAsync(imdb, 1, 1, "tier0");
                else
                    await db.QueueForResolutionAsync(imdb, null, null, "tier0");
                resolutionQueued = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EmbyStreams] Webhook: failed to queue tier0 for {Imdb}", imdb);
            }

            _logger.LogInformation(
                "[EmbyStreams] Webhook: {Imdb} → strm={StrmWritten} resolution={Queued}",
                imdb, strmWritten, resolutionQueued);

            return new WebhookResponse
            {
                Status           = "ok",
                Message          = $"Item {imdb} processed successfully",
                Imdb             = imdb,
                Title            = item.Title,
                StrmWritten      = strmWritten,
                ResolutionQueued = resolutionQueued,
            };
        }

        // ── Private: source re-sync ──────────────────────────────────────────────

        private Task<object> HandleSourceResyncAsync(string source)
        {
            _logger.LogInformation("[EmbyStreams] Webhook: triggering full re-sync for source={Source}", source);

            // Fire CatalogSyncTask in background — Emby task manager would be
            // cleaner but this is immediate and doesn't need ITaskManager injection.
            _ = Task.Run(async () =>
            {
                var config = Plugin.Instance?.Configuration;
                var db     = Plugin.Instance?.DatabaseManager;
                if (config == null || db == null) return;

                using var cts    = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var progress     = new Progress<double>();
                var task         = new CatalogSyncTask(_libraryManager, _logManager);
                try
                {
                    await task.Execute(cts.Token, progress);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[EmbyStreams] Webhook background re-sync failed");
                }
            });

            return Task.FromResult<object>(new WebhookResponse
            {
                Status  = "ok",
                Message = $"Background re-sync queued for source={source}",
            });
        }

        // ── Private: metadata fetch ──────────────────────────────────────────────

        /// <summary>
        /// Tries to resolve title/year/type from the AIOStreams meta endpoint.
        /// Falls back to a placeholder item if the call fails or returns nothing.
        /// </summary>
        private async Task<CatalogItem> FetchOrBuildCatalogItemAsync(
            string imdb, PluginConfiguration config)
        {
            string title    = imdb;
            int?   year     = null;
            string mediaType = "movie"; // default — AIOStreams is mainly movies/series

            try
            {
                using var client = new AioStreamsClient(config, _logger);
                if (client.IsConfigured)
                {
                    // Try movie meta first, then series
                    var meta = await client.GetMetaAsync("movie", imdb, CancellationToken.None);
                    if (meta == null)
                        meta = await client.GetMetaAsync("series", imdb, CancellationToken.None);

                    if (meta != null)
                    {
                        var m = meta.Value;
                        title    = GetString(m, "name") ?? imdb;
                        if (m.TryGetProperty("year", out var yearEl) && yearEl.ValueKind == JsonValueKind.Number)
                            year = yearEl.GetInt32();
                        var type = GetString(m, "type");
                        mediaType = string.Equals(type, "series", StringComparison.OrdinalIgnoreCase) ? "series" : "movie";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[EmbyStreams] Webhook: meta lookup failed for {Imdb} — using placeholder", imdb);
            }

            return new CatalogItem
            {
                ImdbId    = imdb,
                Title     = title,
                Year      = year,
                MediaType = mediaType,
                Source    = "webhook",
                AddedAt   = DateTime.UtcNow.ToString("o"),
                UpdatedAt = DateTime.UtcNow.ToString("o"),
            };
        }

        // ── Private: helpers ─────────────────────────────────────────────────────

        private static string? GetString(JsonElement el, string property)
            => el.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.String
                ? val.GetString() : null;

        private void TriggerLibraryScan()
        {
            try
            {
                _libraryManager.QueueLibraryScan();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EmbyStreams] Webhook: library scan trigger failed");
            }
        }

        /// <summary>
        /// Returns <c>true</c> if the incoming request passes secret validation.
        /// Accepts any of three credential schemes:
        /// <list type="bullet">
        ///   <item><c>Authorization: Bearer &lt;secret&gt;</c></item>
        ///   <item><c>X-Api-Key: &lt;secret&gt;</c></item>
        ///   <item><c>X-Hub-Signature-256: sha256=&lt;hex-hmac-sha256-of-body&gt;</c></item>
        /// </list>
        /// </summary>
        private bool ValidateWebhookSecret(string secret, string body)
        {
            // Bearer token — SEC-9: timing-safe compare to resist timing side-channels
            var authHeader = Request.Headers["Authorization"] ?? string.Empty;
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authHeader.Substring(7).Trim();
                if (FixedTimeEqual(token, secret))
                    return true;
            }

            // X-Api-Key header — SEC-9: timing-safe compare
            var apiKeyHeader = Request.Headers["X-Api-Key"] ?? string.Empty;
            if (FixedTimeEqual(apiKeyHeader.Trim(), secret))
                return true;

            // HMAC-SHA256 (GitHub / Gitea style)
            // The HMAC itself provides timing safety for the body comparison;
            // the hex-string compare below is also timing-safe via FixedTimeEqual.
            var sigHeader = Request.Headers["X-Hub-Signature-256"] ?? string.Empty;
            if (sigHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            {
                var sigHex = sigHeader.Substring(7).Trim();
                try
                {
                    var keyBytes  = Encoding.UTF8.GetBytes(secret);
                    var bodyBytes = Encoding.UTF8.GetBytes(body);
                    using var hmac = new HMACSHA256(keyBytes);
                    var hash    = hmac.ComputeHash(bodyBytes);
                    var hashHex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                    if (FixedTimeEqual(hashHex, sigHex.ToLowerInvariant()))
                        return true;
                }
                catch { /* malformed signature — fall through to reject */ }
            }

            return false;
        }

        /// <summary>
        /// SEC-9: Timing-safe string equality check.
        /// Uses <see cref="CryptographicOperations.FixedTimeEquals"/> on the UTF-8
        /// byte representations so that runtime does not differ based on where the
        /// strings differ, preventing timing side-channel attacks.
        ///
        /// Returns <c>false</c> immediately (without leaking content) when the byte
        /// lengths differ — length mismatch is not a secret.
        /// </summary>
        private static bool FixedTimeEqual(string a, string b)
        {
            var aBytes = Encoding.UTF8.GetBytes(a);
            var bBytes = Encoding.UTF8.GetBytes(b);
            if (aBytes.Length != bBytes.Length) return false;
            return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
        }

        private static object Error(string message)
            => new WebhookResponse { Status = "error", Message = message };

        /// <summary>
        /// SEC-2: Validates an IMDB ID string.
        /// Must start with "tt" (case-insensitive) followed by 1–8 decimal digits only.
        /// Total length is therefore 3–10 characters.
        /// </summary>
        private static bool IsValidImdbId(string imdb)
        {
            if (imdb.Length < 3 || imdb.Length > 10) return false;
            if (!imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) return false;
            for (int i = 2; i < imdb.Length; i++)
                if (!char.IsDigit(imdb[i])) return false;
            return true;
        }
    }
}
