using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using EmbyStreams.Data;
using EmbyStreams.Logging;
using EmbyStreams.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
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
        /// Singleton accessor used by scheduled tasks and services to reach
        /// shared plugin state (DatabaseManager, configuration, logger).
        /// </summary>
        public static Plugin Instance { get; private set; } = null!;

        private readonly ILogger<Plugin> _logger;
        private readonly IApplicationPaths _appPaths;
        private bool _secretEnsured;

        /// <summary>
        /// Shared database manager.  Initialised during plugin construction so it
        /// is ready before any scheduled task or service is invoked.
        /// </summary>
        public DatabaseManager DatabaseManager { get; private set; } = null!;

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

                Configuration.PluginSecret = StreamUrlSigner.GenerateSecret();
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
