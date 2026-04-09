using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Logging;
using EmbyStreams.Services;
using EmbyStreams.Repositories.Interfaces;
using EmbyStreams.Repositories;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("EmbyStreams.Tests")]

namespace EmbyStreams
{
    /// <summary>
    /// EmbyStreams plugin entry point.
    /// Inherits <see cref="BasePlugin{TConfiguration}"/> which handles XML config
    /// persistence at {DataPath}/plugins/configurations/EmbyStreams.xml.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        /// <summary>Stable plugin GUID — never change this after first release.</summary>
        public static readonly Guid PluginGuid = new Guid("3c45a87e-2b4f-4d1a-9e73-8f12c3456789");

        /// <summary>
        /// Static constructor runs when the assembly is first loaded — before any
        /// instance members or type scanning. (Sprint 131: Removed Polly AssemblyResolve handler)
        /// </summary>
        static Plugin()
        {
            // No assembly resolution needed — plugin has no transitive dependencies
        }

        /// <summary>
        /// Singleton accessor used by scheduled tasks and services to reach
        /// shared plugin state (DatabaseManager, configuration, logger).
        /// </summary>
        public static Plugin Instance { get; private set; } = null!;

        private readonly ILogger<Plugin> _logger;
        private readonly ILogManager _logManager;
        private readonly IApplicationPaths _appPaths;
        private bool _secretEnsured;

        /// <summary>
        /// Global synchronization lock for catalog-mutating operations.
        /// Ensures only one catalog sync or doctor task runs at a time.
        /// (Sprint 100A-10)
        /// </summary>
        public static readonly System.Threading.SemaphoreSlim SyncLock =
            new System.Threading.SemaphoreSlim(1, 1);

        /// <summary>
        /// Shared progress streamer for SSE events.
        /// Singleton accessible via Plugin.Instance.ProgressStreamer.
        /// </summary>
        public static ProgressStreamer ProgressStreamer = new();

        /// <summary>
        /// Timestamp when manifest was last fetched. Used for TTL validation.
        /// (Sprint 100A-01)
        /// </summary>
        private static DateTimeOffset _manifestFetchedAt = DateTimeOffset.MinValue;

        /// <summary>
        /// Manifest status: "ok" = loaded and within TTL, "stale" = loaded but past 12-hour TTL, "error" = last fetch failed or never loaded.
        /// (Sprint 102A-01: ManifestStatus state machine)
        /// </summary>
        private static string _manifestStatus = "error";

        /// <summary>
        /// Gets or sets the manifest fetched timestamp.
        /// </summary>
        public static DateTimeOffset ManifestFetchedAt
        {
            get => _manifestFetchedAt;
            set => _manifestFetchedAt = value;
        }

        /// <summary>
        /// Gets the current manifest status.
        /// (Sprint 102A-01: ManifestStatus state machine)
        /// </summary>
        public static string GetManifestStatus() => _manifestStatus;

        /// <summary>
        /// Sets the manifest status. Used by RefreshManifest to update state.
        /// (Sprint 102A-01: ManifestStatus state machine)
        /// </summary>
        internal static void SetManifestStatus(string status)
        {
            _manifestStatus = status;
        }

        /// <summary>
        /// Checks if cached manifest is stale (> 12 hours old) and updates status.
        /// (Sprint 102A-01: ManifestStatus state machine)
        /// </summary>
        internal static void CheckManifestStale()
        {
            if (_manifestFetchedAt != DateTimeOffset.MinValue)
            {
                var age = DateTimeOffset.UtcNow - _manifestFetchedAt;
                if (age > TimeSpan.FromHours(12))
                {
                    _manifestStatus = "stale";
                }
            }
        }

        /// <summary>
        /// Shared database manager.  Initialised during plugin construction so it
        /// is ready before any scheduled task or service is invoked.
        /// </summary>
        public DatabaseManager DatabaseManager { get; private set; } = null!;

        /// <summary>
        /// Version slot repository for quality slot CRUD operations (Sprint 122).
        /// </summary>
        public Data.VersionSlotRepository VersionSlotRepository { get; private set; } = null!;

        /// <summary>
        /// Candidate repository for normalized stream candidates (Sprint 122).
        /// </summary>
        public Data.CandidateRepository CandidateRepository { get; private set; } = null!;

        /// <summary>
        /// Snapshot repository for top-candidate tracking per slot (Sprint 122).
        /// </summary>
        public Data.SnapshotRepository SnapshotRepository { get; private set; } = null!;

        /// <summary>
        /// Materialized version repository for .strm/.nfo pair tracking (Sprint 122).
        /// </summary>
        public Data.MaterializedVersionRepository MaterializedVersionRepository { get; private set; } = null!;

        /// <summary>
        /// Candidate normalizer — parses raw AIOStreams streams into normalized candidates (Sprint 122).
        /// </summary>
        public Services.CandidateNormalizer CandidateNormalizer { get; private set; } = null!;

        /// <summary>
        /// Slot matcher — filters and ranks candidates against slot policies (Sprint 122).
        /// </summary>
        public Services.SlotMatcher SlotMatcher { get; private set; } = null!;

        /// <summary>
        /// Home section tracker for per-user per-rail state.
        /// Sprint 118: Home Screen Rails.
        /// </summary>
        public HomeSectionTracker HomeSectionTracker { get; private set; } = null!;

        /// <summary>
        /// Home section manager for adding rails to Emby home screen.
        /// Sprint 118: Home Screen Rails.
        /// </summary>
        public HomeSectionManager HomeSectionManager { get; private set; } = null!;

        /// <summary>
        /// Catalog repository for catalog item operations (Sprint 104D-02).
        /// Delegates to DatabaseManager - temporary adapter during split.
        /// </summary>
        public ICatalogRepository CatalogRepository { get; private set; } = null!;

        /// <summary>
        /// Pin repository interface for item pin/unpin operations.
        /// Delegates to DatabaseManager (Sprint 104A-04).
        /// </summary>
        public IPinRepository PinRepository => DatabaseManager;

        /// <summary>
        /// Resolution cache repository interface for stream URL caching.
        /// Delegates to DatabaseManager (Sprint 104A-04).
        /// </summary>
        public IResolutionCacheRepository ResolutionCacheRepository => DatabaseManager;

        /// <summary>
        /// Initialises the EmbyStreams plugin.
        /// Emby injects <paramref name="appPaths"/>, <paramref name="xmlSerializer"/>
        /// and <paramref name="loggerFactory"/> automatically when loading the plugin.
        /// </summary>
        public Plugin(
            IApplicationPaths appPaths,
            IXmlSerializer xmlSerializer,
            ILogManager logManager)
            : base(appPaths, xmlSerializer)
        {
            Instance = this;
            _appPaths = appPaths;
            _logManager = logManager;
            _logger = new EmbyLoggerAdapter<Plugin>(logManager.GetLogger("EmbyStreams"));

            InitialiseDatabaseManager(appPaths);
            // Defer EnsurePluginSecret to first config access - ApplicationPaths may not be ready yet
        }

        /// <inheritdoc/>
        public override string Name => "EmbyStreams";

        /// <inheritdoc/>
        public override Guid Id => PluginGuid;

        /// <inheritdoc/>
        public override string Description =>
            "Real-Debrid streaming via AIOStreams — paste URL, press Play, done. (The answer is 42.)";

        /// <summary>
        /// Plugin version read from plugin.json manifest.
        /// Returns the version string or "unknown" if unavailable.
        /// Note: Named PluginVersion to avoid conflict with BasePlugin.Version.
        /// </summary>
        public string PluginVersion
        {
            get
            {
                try
                {
                    // Assembly.Location returns empty string in .NET Core load contexts
                    // DataPath is ~/emby-dev-data/data, plugins are at ~/emby-dev-data/plugins
                    // So we need to go up one level from DataPath
                    if (_appPaths?.DataPath == null)
                        return "unknown";

                    // Get parent directory of DataPath, then add "plugins"
                    var basePath = Directory.GetParent(_appPaths.DataPath)?.FullName ?? _appPaths.DataPath;
                    var pluginsDir = Path.Combine(basePath, "plugins");
                    var jsonPath = Path.Combine(pluginsDir, "plugin.json");

                    if (!File.Exists(jsonPath))
                        return "unknown";

                    var json = File.ReadAllText(jsonPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("version", out var versionProp))
                        return versionProp.GetString() ?? "unknown";
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[EmbyStreams] Failed to read version from plugin.json");
                }
                return "unknown";
            }
        }


        // ── IHasWebPages ────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "EmbyStreams",
                    EmbeddedResourcePath = "EmbyStreams.Configuration.configurationpage.html",
                    IsMainConfigPage = true,
                    EnableInMainMenu = true,
                    DisplayName = "EmbyStreams"
                },
                new PluginPageInfo
                {
                    Name = "EmbyStreamsConfigJS",
                    EmbeddedResourcePath = "EmbyStreams.Configuration.configurationpage.js"
                },
                new PluginPageInfo
                {
                    Name = "Wizard",
                    DisplayName = "EmbyStreams Setup Wizard",
                    EnableInMainMenu = true
                },
                new PluginPageInfo
                {
                    Name = "ContentManagement",
                    DisplayName = "EmbyStreams Content Management",
                    EnableInMainMenu = true
                },
                new PluginPageInfo
                {
                    Name = "MyLibrary",
                    DisplayName = "EmbyStreams My Library",
                    EnableInMainMenu = true
                }
            };
        }

        // ── IHasThumbImage ───────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".thumb.png")!;
        }

        /// <inheritdoc/>
        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        // ── Public helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Ensures the PluginSecret is initialized. Should be called by services
        /// that need stream signing before first use.
        /// </summary>
        public void EnsureInitialization()
        {
            EnsurePluginSecret();
        }

        /// <summary>
        /// Async version that ensures PluginSecret is initialized before .strm file writes.
        /// Returns true if secret exists (was already set or was just generated), false if failed.
        /// </summary>
        public Task<bool> EnsureInitializedAsync()
        {
            EnsurePluginSecret();
            // Check if secret actually exists after EnsurePluginSecret
            var success = !string.IsNullOrEmpty(Configuration?.PluginSecret);
            if (!success)
            {
                _logger?.LogWarning("[EmbyStreams] PluginSecret is empty after EnsurePluginSecret — .strm files will use unauthenticated URLs");
            }
            else
            {
                _logger?.LogInformation("[EmbyStreams] PluginSecret confirmed ready for .strm file generation");
            }
            return Task.FromResult(success);
        }

        /// <summary>
        /// Checks whether the Emby Anime Plugin is installed by scanning
        /// the plugins directory for its assembly folder.
        /// The plugin typically lives at {DataPath}/plugins/Emby.Plugins.Anime/.
        /// </summary>
        public static bool IsAnimePluginInstalled()
        {
            try
            {
                var instance = Instance;
                if (instance == null) return false;

                var pluginsDir = Path.Combine(instance._appPaths.DataPath, "plugins");
                if (!Directory.Exists(pluginsDir)) return false;

                foreach (var dir in Directory.EnumerateDirectories(pluginsDir))
                {
                    var dirName = Path.GetFileName(dir);
                    if (dirName.IndexOf("anime", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (Directory.GetFiles(dir, "*.dll").Length > 0)
                            return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        // ── Private helpers ─────────────────────────────────────────────────────

        private void InitialiseDatabaseManager(IApplicationPaths appPaths)
        {
            try
            {
                var dbDirectory = Path.Combine(appPaths.DataPath, "EmbyStreams");
                DatabaseManager = new DatabaseManager(dbDirectory, _logger);
                DatabaseManager.Initialise();
                _logger.LogInformation("[EmbyStreams] Database initialised at {DbDir}", dbDirectory);

                // Initialise repository layer (Sprint 104D-02)
                CatalogRepository = new CatalogRepository(DatabaseManager, _logManager);
                _logger.LogInformation("[EmbyStreams] Repository layer initialised");

                // Initialise version slot repository (Sprint 122: Versioned Playback)
                VersionSlotRepository = new Data.VersionSlotRepository(dbDirectory, _logger);
                _logger.LogInformation("[EmbyStreams] Version slot repository initialised");

                // Initialise versioned playback repositories (Sprint 127: Registration)
                CandidateRepository = new Data.CandidateRepository(DatabaseManager, _logger);
                SnapshotRepository = new Data.SnapshotRepository(DatabaseManager, _logger);
                MaterializedVersionRepository = new Data.MaterializedVersionRepository(DatabaseManager, _logger);
                _logger.LogInformation("[EmbyStreams] Versioned playback repositories initialised");

                // Initialise versioned playback services (Sprint 127: Registration)
                CandidateNormalizer = new Services.CandidateNormalizer(_logger);
                SlotMatcher = new Services.SlotMatcher();
                _logger.LogInformation("[EmbyStreams] Versioned playback services initialised");

                // Initialise home section tracker (Sprint 118: Home Screen Rails)
                HomeSectionTracker = new HomeSectionTracker(DatabaseManager, new EmbyLoggerAdapter<HomeSectionTracker>(_logManager.GetLogger("HomeSectionTracker")));
                _logger.LogInformation("[EmbyStreams] Home section tracker initialised");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EmbyStreams] Failed to initialise database — plugin may not function correctly");
            }
        }

        /// <summary>
        /// Generates and persists a PluginSecret if one does not already exist.
        /// Called lazily on first config access to ensure ApplicationPaths is ready.
        /// Safe to call multiple times - will only execute once.
        /// </summary>
        private void EnsurePluginSecret()
        {
            if (_secretEnsured)
                return;

            try
            {
                // Guard against ApplicationPaths not being ready yet
                if (ApplicationPaths?.PluginConfigurationsPath == null)
                {
                    _logger.LogWarning("[EmbyStreams] Deferring PluginSecret — ApplicationPaths not ready yet");
                    return;
                }

                if (!string.IsNullOrEmpty(Configuration.PluginSecret))
                {
                    _secretEnsured = true;
                    return;
                }

                Configuration.PluginSecret = PlaybackTokenService.GenerateSecret();
                SaveConfiguration();
                _secretEnsured = true;
                _logger.LogInformation("[EmbyStreams] PluginSecret generated and saved");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EmbyStreams] Failed to generate PluginSecret — stream signing will not work");
                _secretEnsured = true; // Don't retry on error
            }
        }
    }
}
