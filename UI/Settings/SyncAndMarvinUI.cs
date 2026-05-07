using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;

namespace InfiniteDrive.UI.Settings
{
    public class SyncAndMarvinUI : EditableOptionsBase
    {
        public const string RunMarvinNowCommand = nameof(RunMarvinNowCommand);
        public const string TogglePruningCommand = nameof(TogglePruningCommand);

        public override string EditorTitle => "Marvin";
        public override string EditorDescription =>
            "Schedule, pruning, and rate limits for the Marvin background process.";

        // ═══════════════════════════════════════════════════════════════
        // Section 0: Marvin Status (top)
        // ═══════════════════════════════════════════════════════════════

        public StatusItem MarvinStatus { get; set; } = new StatusItem("Marvin", "Idle", ItemStatus.None);

        public SpacerItem SpacerStatus { get; set; } = new SpacerItem();

        // ═══════════════════════════════════════════════════════════════
        // Section 1: Marvin Process Schedule
        // ═══════════════════════════════════════════════════════════════

        public CaptionItem CaptionSchedule { get; set; } = new CaptionItem("Schedule");

        [DisplayName("Run interval (minutes)")]
        [Description("How often Marvin's main loop fires. Default: 10.")]
        public int MarvinProcessIntervalMinutes { get; set; } = 10;

        [DisplayName("Batch size")]
        [Description("Number of items Marvin resolves per batch. Default: 42.")]
        public int StreamResolutionBatchSize { get; set; } = 42;

        public ButtonItem RunMarvinNowButton { get; set; } = new ButtonItem("Run Marvin Now")
        {
            Icon = IconNames.play_arrow,
            Data1 = RunMarvinNowCommand,
        };

        // ═══════════════════════════════════════════════════════════════
        // Section 2: Pruning
        // ═══════════════════════════════════════════════════════════════

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionPruning { get; set; } = new CaptionItem("Pruning");

        [DisplayName("Respect user playlists when pruning")]
        [Description("When enabled, items in user playlists and self-managed collections are never pruned.")]
        public bool RespectPlaylistsWhenPruning { get; set; } = true;

        [DisplayName("Auto-deduplicate against physical media in other libraries")]
        [Description("When enabled, virtual items are suppressed if the same title exists as physical media elsewhere.")]
        public bool AutoDeduplicatePhysicalMedia { get; set; } = true;

        public LabelItem PruningSummary { get; set; } = new LabelItem(
            "Items sync continuously. User playlists are respected. Physical media in other libraries takes precedence.");

        // ═══════════════════════════════════════════════════════════════
        // Section 3: Rate Limits
        // ═══════════════════════════════════════════════════════════════

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionRateLimit { get; set; } = new CaptionItem("Rate Limits");

        [DisplayName("Actions per hour")]
        [Description(
            "Limits how aggressively Marvin runs to stay a good citizen with AIOStreams and debrid providers. " +
            "Default: 360.")]
        public int MarvinActionsPerHour { get; set; } = 360;

    }
}
