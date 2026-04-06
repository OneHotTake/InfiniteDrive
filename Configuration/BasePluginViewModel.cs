using System;
using MediaBrowser.Model.Plugins;

namespace EmbyStreams.Configuration
{
    /// <summary>
    /// Base class for all Plugin UI ViewModels.
    /// Provides common functionality for all configuration pages.
    /// </summary>
    public abstract class BasePluginViewModel : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets the plugin version.
        /// </summary>
        public string PluginVersion { get; set; } = "0.51.0.0";

        /// <summary>
        /// Gets or sets the last sync timestamp.
        /// </summary>
        public DateTimeOffset? LastSyncAt { get; set; }

        /// <summary>
        /// Gets or sets the plugin status.
        /// </summary>
        public string Status { get; set; } = "OK";
    }
}
