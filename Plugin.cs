using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using InfiniteDrive.Services;
using InfiniteDrive.Repositories.Interfaces;
using InfiniteDrive.Repositories;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("InfiniteDrive.Tests")]

namespace InfiniteDrive
{
    /// <summary>
    /// InfiniteDrive plugin entry point.
    /// Inherits <see cref="BasePlugin{TConfiguration}"/> which handles XML config
    /// persistence at {DataPath}/plugins/configurations/EmbyStreams.xml.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage, IHasUIPages
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
        private System.Collections.Generic.List<IPluginUIPageController> _uiPages;

        /// <summary>
        /// Shared logger for use by auto-discovered providers (e.g. AioMetadataProvider).
        /// </summary>
        public ILogger<Plugin> Logger => _logger;

        /// <summary>
        /// Global synchronization lock for catalog-mutating operations.
        /// Ensures only one catalog sync or Marvin task runs at a time.
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
        /// Manifest state container — single authority for status, fetched timestamp, staleness.
        /// Sprint 360: replaces scattered statics + static methods.
        /// </summary>
        public static ManifestState Manifest { get; } = new();

        /// <summary>
        /// Pipeline phase tracker — shared operational picture for admin UI + diagnostics.
        /// Sprint 361: In-memory snapshot, no DB writes.
        /// </summary>
        public static PipelinePhaseTracker Pipeline { get; } = new();

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
        /// StrmWriterService — unified .strm file writer.
        /// All .strm writes go through this singleton (Sprint 156).
        /// </summary>
        public Services.StrmWriterService StrmWriterService { get; private set; } = null!;

        /// <summary>
        /// Resolution cache repository interface for stream URL caching.
        /// Delegates to DatabaseManager (Sprint 104A-04).
        /// </summary>
        public IResolutionCacheRepository ResolutionCacheRepository => DatabaseManager;

        /// <summary>
        /// Cooldown gate for HTTP throttling. Replaces scattered Task.Delay(ApiCallDelayMs).
        /// Initialised during <see cref="InfiniteDriveInitializationService.Run"/>.
        /// </summary>
        public Services.CooldownGate CooldownGate { get; internal set; } = null!;

        /// <summary>
        /// Stream probe service for checking stream availability before serving to users (Sprint 159).
        /// </summary>
        public Services.StreamProbeService StreamProbeService { get; private set; } = null!;

        /// <summary>
        /// ID resolver service for normalising manifest IDs to canonical provider IDs (Sprint 160).
        /// Calls source addon /meta endpoint, then AIOMetadata as fallback.
        /// </summary>
        public Services.IdResolverService IdResolverService { get; private set; } = null!;

        /// <summary>
        /// Certification resolver for fetching MPAA/TV ratings from TMDB (Sprint 209).
        /// Used for parental filtering in Discover browse/search.
        /// </summary>
        public Services.CertificationResolver CertificationResolver { get; private set; } = null!;

        /// <summary>
        /// Shared resolver health tracker with circuit breaker (Sprint 310).
        /// Singleton shared across ResolverService, StreamResolutionHelper, etc.
        /// </summary>
        public Services.ResolverHealthTracker ResolverHealthTracker { get; private set; } = null!;

        /// <summary>
        /// Active provider state for self-healing failover (Sprint 311).
        /// Tracks whether primary or secondary provider is currently active.
        /// </summary>
        public Models.ActiveProviderState ActiveProviderState { get; private set; } = new();

        /// <summary>
        /// Centralized state engine for system health evaluation (Sprint 401).
        /// Initialized during InitialiseDatabaseManager().
        /// </summary>
        public Services.SystemStateService SystemStateService { get; private set; } = null!;

        /// <summary>
        /// Plugin constructor — lightweight only per Emby conventions.
        /// Heavy initialization (database, repositories, PluginSecret) is deferred to
        /// InfiniteDriveInitializationService.Run() via IServerEntryPoint.
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
            _logger = new EmbyLoggerAdapter<Plugin>(logManager.GetLogger("InfiniteDrive"));
            //
            // NOTE (Sprint 152): DatabaseManager.Initialise() and PluginSecret generation
            // are now called from InfiniteDriveInitializationService.Run() (IServerEntryPoint),
            // not from this constructor. IServerEntryPoint.Run() is called before any
            // scheduled tasks fire, so this is safe.
            //
        }

        /// <inheritdoc/>
        public override string Name => "InfiniteDrive";

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
                    _logger?.LogWarning(ex, "[InfiniteDrive] Failed to read version from plugin.json");
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
                    Name = "InfiniteDrive",
                    EmbeddedResourcePath = "InfiniteDrive.Configuration.configurationpage.html",
                    IsMainConfigPage = true,
                    EnableInMainMenu = true,
                    DisplayName = "InfiniteDrive"
                },
                new PluginPageInfo
                {
                    Name = "InfiniteDriveConfigJS",
                    EmbeddedResourcePath = "InfiniteDrive.Configuration.configurationpage.js"
                },
                // Sprint 210: User-facing Discover UI
                new PluginPageInfo
                {
                    Name = "InfiniteDiscover",
                    EmbeddedResourcePath = "InfiniteDrive.Configuration.discoverpage.html",
                    IsMainConfigPage = false,
                    EnableInMainMenu = true,
                    DisplayName = "Discover",
                    MenuIcon = "explore"
                },
                new PluginPageInfo
                {
                    Name = "InfiniteDiscoverJS",
                    EmbeddedResourcePath = "InfiniteDrive.Configuration.discoverpage.js"
                }
            };
        }

        // ── IHasUIPages ────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public System.Collections.Generic.IReadOnlyCollection<IPluginUIPageController> UIPageControllers
        {
            get
            {
                if (_uiPages == null)
                {
                    _uiPages = new System.Collections.Generic.List<IPluginUIPageController>();
                    _uiPages.Add(new Configuration.UI.MainPageController(GetPluginInfo()));
                }
                return _uiPages.AsReadOnly();
            }
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
                _logger?.LogWarning("[InfiniteDrive] PluginSecret is empty after EnsurePluginSecret — .strm files will use unauthenticated URLs");
            }
            else
            {
                _logger?.LogInformation("[InfiniteDrive] PluginSecret confirmed ready for .strm file generation");
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

        public void InitialiseDatabaseManager()
        {
            try
            {
                var dbDirectory = Path.Combine(_appPaths.DataPath, "InfiniteDrive");
                DatabaseManager = new DatabaseManager(dbDirectory, _logger);
                DatabaseManager.Initialise();
                _logger.LogInformation("[InfiniteDrive] Database initialised at {DbDir}", dbDirectory);

                // Initialise SystemStateService (Sprint 401: State Engine)
                SystemStateService = new Services.SystemStateService(DatabaseManager);
                _logger.LogInformation("[InfiniteDrive] SystemStateService initialised");

                // Initialise repository layer (Sprint 104D-02)
                CatalogRepository = new CatalogRepository(DatabaseManager, _logManager);
                _logger.LogInformation("[InfiniteDrive] Repository layer initialised");

                // Initialise version slot repository (Sprint 122: Versioned Playback)
                VersionSlotRepository = new Data.VersionSlotRepository(dbDirectory, _logger);
                _logger.LogInformation("[InfiniteDrive] Version slot repository initialised");

                // Initialise versioned playback repositories (Sprint 127: Registration)
                CandidateRepository = new Data.CandidateRepository(DatabaseManager, _logger);
                SnapshotRepository = new Data.SnapshotRepository(DatabaseManager, _logger);
                MaterializedVersionRepository = new Data.MaterializedVersionRepository(DatabaseManager, _logger);
                _logger.LogInformation("[InfiniteDrive] Versioned playback repositories initialised");

                // Initialise versioned playback services (Sprint 127: Registration)
                CandidateNormalizer = new Services.CandidateNormalizer(_logger);
                SlotMatcher = new Services.SlotMatcher();
                _logger.LogInformation("[InfiniteDrive] Versioned playback services initialised");

                // Initialise home section tracker (Sprint 118: Home Screen Rails)
                HomeSectionTracker = new HomeSectionTracker(DatabaseManager, new EmbyLoggerAdapter<HomeSectionTracker>(_logManager.GetLogger("HomeSectionTracker")));
                _logger.LogInformation("[InfiniteDrive] Home section tracker initialised");

                // Initialise StrmWriterService (Sprint 156: Unified Write Path)
                StrmWriterService = new Services.StrmWriterService(_logManager, DatabaseManager);
                _logger.LogInformation("[InfiniteDrive] StrmWriterService initialised");

                // Initialise StreamProbeService (Sprint 159: Stream Availability Probe)
                StreamProbeService = new Services.StreamProbeService(
                    new EmbyLoggerAdapter<Services.StreamProbeService>(_logManager.GetLogger("StreamProbeService")));
                _logger.LogInformation("[InfiniteDrive] StreamProbeService initialised");

                // Initialise IdResolverService (Sprint 160: Robust ID Normalisation)
                IdResolverService = new Services.IdResolverService(_logManager);
                _logger.LogInformation("[InfiniteDrive] IdResolverService initialised");

                // Initialise CertificationResolver (Sprint 209: Parental Filtering)
                CertificationResolver = new Services.CertificationResolver(
                    new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }),
                    new EmbyLoggerAdapter<Services.CertificationResolver>(_logManager.GetLogger("CertificationResolver")));
                _logger.LogInformation("[InfiniteDrive] CertificationResolver initialised");

                // Initialise ResolverHealthTracker (Sprint 310: Shared singleton)
                ResolverHealthTracker = new Services.ResolverHealthTracker(
                    new EmbyLoggerAdapter<Services.ResolverHealthTracker>(_logManager.GetLogger("ResolverHealthTracker")),
                    DatabaseManager);
                ResolverHealthTracker.RestoreState();
                _logger.LogInformation("[InfiniteDrive] ResolverHealthTracker initialised");

                // Sprint 350: Restore active provider state from database
                try
                {
                    var savedProvider = DatabaseManager.GetActiveProvider();
                    if (savedProvider == "Secondary")
                    {
                        ActiveProviderState.Current = Models.ActiveProvider.Secondary;
                        _logger.LogInformation("[InfiniteDrive] Restored ActiveProvider=Secondary from database");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[InfiniteDrive] Could not restore active provider state");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] Failed to initialise database — plugin may not function correctly");
            }
        }

        /// <summary>
        /// Generates and persists a PluginSecret if one does not already exist.
        /// Called lazily on first config access to ensure ApplicationPaths is ready.
        /// Safe to call multiple times - will only execute once.
        /// </summary>
        public void EnsurePluginSecret()
        {
            if (_secretEnsured)
                return;

            try
            {
                // Guard against ApplicationPaths not being ready yet
                if (ApplicationPaths?.PluginConfigurationsPath == null)
                {
                    _logger.LogWarning("[InfiniteDrive] Deferring PluginSecret — ApplicationPaths not ready yet");
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
                _logger.LogInformation("[InfiniteDrive] PluginSecret generated and saved");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InfiniteDrive] Failed to generate PluginSecret — stream signing will not work");
                _secretEnsured = true; // Don't retry on error
            }
        }
    }
}
