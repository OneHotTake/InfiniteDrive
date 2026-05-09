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
            "Wipes cached stream URL lookups. Marvin will re-resolve on the next pass.");

        public ButtonItem ClearCacheButton { get; set; } = new ButtonItem("Clear Stream Resolution Cache")
        {
            Icon = IconNames.delete_sweep,
            Data1 = ClearCacheCommand,
            ConfirmationPrompt = "Clear all cached stream URL lookups? Marvin will re-resolve streams on its next run.",
        };

        // ── Section 3: Last Resort ───────────────────────────────────────────

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionLastResort { get; set; } = new CaptionItem("Last Resort");

        public LabelItem LastResortHelp { get; set; } = new LabelItem(
            "These actions are permanent. There is no undo.");

        public ButtonItem RebuildLibrariesButton { get; set; } = new ButtonItem("Force Full Rebuild")
        {
            Icon = IconNames.refresh,
            Data1 = RebuildLibrariesCommand,
            ConfirmationPrompt = "Re-sync all catalogs and rewrite every .strm file from scratch. This may take several minutes.",
        };

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();

        public ButtonItem ResetAllDataButton { get; set; } = new ButtonItem("Wipe Library Data")
        {
            Icon = IconNames.warning,
            Data1 = ResetAllDataCommand,
            ConfirmationPrompt = "Deletes all catalog items, .strm files, and cached stream data. Your settings are preserved. This cannot be undone.",
        };

        public SpacerItem Spacer3 { get; set; } = new SpacerItem();

        public ButtonItem ResetFactoryDefaultsButton { get; set; } = new ButtonItem("Reset to Factory Defaults")
        {
            Icon = IconNames.restore,
            Data1 = ResetFactoryDefaultsCommand,
            ConfirmationPrompt = "Resets every setting and deletes all data. InfiniteDrive will be as if it was just installed. This cannot be undone.",
        };

        public StatusItem ActionStatus { get; set; } = new StatusItem("Last Action", "None", ItemStatus.None);
    }
}
