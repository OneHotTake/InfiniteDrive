using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;

namespace InfiniteDrive.UI.Settings
{
    public class AdvancedUI : EditableOptionsBase
    {
        public const string ShowAdvancedCommand = nameof(ShowAdvancedCommand);
        public const string ClearCacheCommand = nameof(ClearCacheCommand);
        public const string ResetAllDataCommand = nameof(ResetAllDataCommand);
        public const string RebuildLibrariesCommand = nameof(RebuildLibrariesCommand);
        public const string ResetFactoryDefaultsCommand = nameof(ResetFactoryDefaultsCommand);

        public override string EditorTitle => "Advanced";
        public override string EditorDescription =>
            "Power-user and maintenance options. You don't need to touch this unless you have a specific reason.";

        // ── DON'T PANIC Header ────────────────────────────────────────────────

        public CaptionItem DontPanicHeader { get; set; } = new CaptionItem("DON'T PANIC");

        // ── Toggle ──────────────────────────────────────────────────────────────

        [DisplayName("Show advanced settings")]
        [Description("Enable to reveal logging, cache, and maintenance options below.")]
        public bool ShowAdvanced { get; set; } = false;

        // ── Section 1: Logging & Debugging ──────────────────────────────────────

        public CaptionItem CaptionLogging { get; set; } = new CaptionItem("Logging & Debugging");

        [DisplayName("Log Level")]
        [Description("Minimum log verbosity level for InfiniteDrive. Default: Info.")]
        public string PluginLogLevel { get; set; } = "Info";

        public ButtonItem ClearCacheButton { get; set; } = new ButtonItem("Clear All Caches")
        {
            Icon = IconNames.delete_sweep,
            Data1 = ClearCacheCommand,
            ConfirmationPrompt = "Clear the resolution cache and vacuum the database?",
        };

        public StatusItem CacheStatus { get; set; } = new StatusItem("Cache", "Idle", ItemStatus.None);

        // ── Section 2: Cache Settings ───────────────────────────────────────────

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionCache { get; set; } = new CaptionItem("Cache Settings");

        [DisplayName("Cache Refresh Interval (days)")]
        [Description(
            "Marvin will automatically refresh the full stream URLs stored in the SQLite database after this many days. " +
            "Enable proxy mode on your AIOStreams instance (highly recommended) for best results.")]
        public int CacheRefreshIntervalDays { get; set; } = 30;

        // ── Section 3: Maintenance & Reset ──────────────────────────────────────

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionMaintenance { get; set; } = new CaptionItem("Maintenance & Reset");

        public ButtonItem ResetAllDataButton { get; set; } = new ButtonItem("Reset All InfiniteDrive Data")
        {
            Icon = IconNames.warning,
            Data1 = ResetAllDataCommand,
            ConfirmationPrompt = "WARNING: This will delete all catalog items, stream candidates, and cached data. Your .strm files will also be removed. This cannot be undone. Continue?",
        };

        public ButtonItem RebuildLibrariesButton { get; set; } = new ButtonItem("Rebuild Libraries from Scratch")
        {
            Icon = IconNames.refresh,
            Data1 = RebuildLibrariesCommand,
            ConfirmationPrompt = "Rebuild all libraries from scratch? This will clear existing data and trigger a full catalog sync.",
        };

        public ButtonItem ResetFactoryDefaultsButton { get; set; } = new ButtonItem("Reset to Factory Defaults")
        {
            Icon = IconNames.restore,
            Data1 = ResetFactoryDefaultsCommand,
            ConfirmationPrompt = "Reset all InfiniteDrive settings to factory defaults? This will clear all configuration and data. This cannot be undone. Continue?",
        };

        public StatusItem MaintenanceStatus { get; set; } = new StatusItem("Maintenance", "Idle", ItemStatus.None);

    }
}
