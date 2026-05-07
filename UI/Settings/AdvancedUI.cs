using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;

namespace InfiniteDrive.UI.Settings
{
    public class AdvancedUI : EditableOptionsBase
    {
        public const string ClearCacheCommand = nameof(ClearCacheCommand);
        public const string ResetAllDataCommand = nameof(ResetAllDataCommand);
        public const string RebuildLibrariesCommand = nameof(RebuildLibrariesCommand);
        public const string ResetFactoryDefaultsCommand = nameof(ResetFactoryDefaultsCommand);

        public override string EditorTitle => "Advanced";
        public override string EditorDescription =>
            "Power-user and maintenance options. You don't need to touch this unless you have a specific reason.";

        // ── Section 1: Logging ───────────────────────────────────────────────

        public CaptionItem CaptionLogging { get; set; } = new CaptionItem("Logging");

        [DisplayName("Log Level")]
        [Description("Minimum log verbosity for InfiniteDrive entries in the Emby server log. Use Debug when diagnosing a problem; Info for normal operation.")]
        [SelectItemsSource(nameof(LogLevelOptions))]
        public string PluginLogLevel { get; set; } = "Info";

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> LogLevelOptions { get; set; } = new List<EditorSelectOption>
        {
            new() { Value = "Info",  Name = "Info (normal)",  IsEnabled = true },
            new() { Value = "Debug", Name = "Debug (verbose)", IsEnabled = true },
        };

        // ── Section 2: Cache ─────────────────────────────────────────────────

        public SpacerItem Spacer0 { get; set; } = new SpacerItem();
        public CaptionItem CaptionCache { get; set; } = new CaptionItem("Cache");

        public LabelItem ClearCacheHelp { get; set; } = new LabelItem(
            "Wipes cached stream URL lookups from the database. " +
            "Marvin will re-resolve all streams on its next cycle. " +
            "Safe to run at any time — no content is deleted.");

        public ButtonItem ClearCacheButton { get; set; } = new ButtonItem("Clear Stream Resolution Cache")
        {
            Icon = IconNames.delete_sweep,
            Data1 = ClearCacheCommand,
            ConfirmationPrompt = "Clear all cached stream URL lookups? Marvin will re-resolve streams on its next run.",
        };

        // ── Section 3: Reset ─────────────────────────────────────────────────

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionReset { get; set; } = new CaptionItem("Reset");

        public LabelItem RebuildHelp { get; set; } = new LabelItem(
            "REBUILD LIBRARIES: Triggers a full Marvin sync to re-populate your Emby libraries from scratch. " +
            "No data or settings are deleted — this is a safe re-index operation. " +
            "Use this if your libraries look stale or items are missing.");

        public ButtonItem RebuildLibrariesButton { get; set; } = new ButtonItem("Rebuild Libraries")
        {
            Icon = IconNames.refresh,
            Data1 = RebuildLibrariesCommand,
            ConfirmationPrompt = "Trigger a full library rebuild? No data is deleted — Marvin will re-sync all catalogs and recreate .strm files.",
        };

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();

        public LabelItem ResetDataHelp { get; set; } = new LabelItem(
            "RESET ALL DATA: Deletes every catalog item, .strm file, and cached stream URL from disk and the database. " +
            "Your settings (manifest URLs, library paths, quality tiers) are preserved. " +
            "Use this to start completely fresh while keeping your configuration.");

        public ButtonItem ResetAllDataButton { get; set; } = new ButtonItem("Reset All InfiniteDrive Data")
        {
            Icon = IconNames.warning,
            Data1 = ResetAllDataCommand,
            ConfirmationPrompt = "WARNING: Deletes all catalog items, .strm files, and cached stream data. Your settings are kept. This cannot be undone. Continue?",
        };

        public SpacerItem Spacer3 { get; set; } = new SpacerItem();

        public LabelItem FactoryHelp { get; set; } = new LabelItem(
            "FACTORY RESET: Wipes ALL settings AND all data back to the out-of-box state — " +
            "as if you had just installed the plugin. " +
            "Manifest URLs, library paths, quality tiers, API keys — everything is cleared. " +
            "Use this only as a last resort.");

        public ButtonItem ResetFactoryDefaultsButton { get; set; } = new ButtonItem("Factory Reset")
        {
            Icon = IconNames.restore,
            Data1 = ResetFactoryDefaultsCommand,
            ConfirmationPrompt = "FACTORY RESET: Wipes all settings AND all data. The plugin will need to be fully reconfigured. This cannot be undone. Continue?",
        };

        public StatusItem ActionStatus { get; set; } = new StatusItem("Last Action", "None", ItemStatus.None);
    }
}
