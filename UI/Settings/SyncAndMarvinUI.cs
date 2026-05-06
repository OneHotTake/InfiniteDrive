using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;

namespace InfiniteDrive.UI.Settings
{
    public class SyncAndMarvinUI : EditableOptionsBase
    {
        public const string RunMarvinNowCommand = nameof(RunMarvinNowCommand);
        public const string TogglePruningCommand = nameof(TogglePruningCommand);
        public const string AddBucketCommand = nameof(AddBucketCommand);
        public const string RemoveBucketCommand = nameof(RemoveBucketCommand);

        public override string EditorTitle => "Sync & Marvin";
        public override string EditorDescription =>
            "Marvin process schedule, version quality buckets, pruning rules, and rate-limit safety.";

        // ═══════════════════════════════════════════════════════════════
        // Section 1: Marvin Process Schedule
        // ═══════════════════════════════════════════════════════════════

        public CaptionItem CaptionSchedule { get; set; } = new CaptionItem("Marvin Process Schedule");

        [DisplayName("Marvin Process Interval (minutes)")]
        [Description("How often Marvin's main loop fires. Default: 10.")]
        public int MarvinProcessIntervalMinutes { get; set; } = 10;

        [DisplayName("Stream Resolution Batch Size")]
        [Description("Number of items Marvin resolves per batch. Default: 42.")]
        public int StreamResolutionBatchSize { get; set; } = 42;

        public ButtonItem RunMarvinNowButton { get; set; } = new ButtonItem("Run Marvin Now")
        {
            Icon = IconNames.play_arrow,
            Data1 = RunMarvinNowCommand,
        };

        public StatusItem MarvinStatus { get; set; } = new StatusItem("Marvin", "Idle", ItemStatus.None);

        // ═══════════════════════════════════════════════════════════════
        // Section 2: Multi-Version STRM — Quality Buckets
        // ═══════════════════════════════════════════════════════════════

        public SpacerItem SpacerVersion { get; set; } = new SpacerItem();
        public CaptionItem CaptionVersions { get; set; } = new CaptionItem("Multi-Version Quality Buckets");

        public LabelItem BucketHelp { get; set; } = new LabelItem(
            "Buckets are matched in order — earlier buckets get priority. " +
            "Remaining slots fill with next-best streams.");

        public GenericItemList BucketList { get; set; } = new GenericItemList();

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> ResolutionOptions { get; set; } = new List<EditorSelectOption>
        {
            new() { Value = "4K",    Name = "4K",    IsEnabled = true },
            new() { Value = "1080p", Name = "1080p", IsEnabled = true },
            new() { Value = "720p",  Name = "720p",  IsEnabled = true },
            new() { Value = "SD",    Name = "SD",    IsEnabled = true },
        };

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> AudioOptions { get; set; } = new List<EditorSelectOption>
        {
            new() { Value = "Any Audio",            Name = "Any Audio",            IsEnabled = true },
            new() { Value = "Lossless/Premium",     Name = "Lossless/Premium",     IsEnabled = true },
            new() { Value = "5.1/7.1 (Surround)",   Name = "5.1/7.1 (Surround)",   IsEnabled = true },
            new() { Value = "DD/DTS (Compressed)",   Name = "DD/DTS (Compressed)",   IsEnabled = true },
            new() { Value = "Stereo/2.0",            Name = "Stereo/2.0",            IsEnabled = true },
        };

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> CountOptions { get; set; } = new List<EditorSelectOption>
        {
            new() { Value = "1", Name = "1", IsEnabled = true },
            new() { Value = "2", Name = "2", IsEnabled = true },
            new() { Value = "3", Name = "3", IsEnabled = true },
            new() { Value = "4", Name = "4", IsEnabled = true },
            new() { Value = "5", Name = "5", IsEnabled = true },
            new() { Value = "6", Name = "6", IsEnabled = true },
            new() { Value = "7", Name = "7", IsEnabled = true },
            new() { Value = "8", Name = "8", IsEnabled = true },
        };

        [DisplayName("Resolution")]
        [Description("Resolution to match for this bucket.")]
        [SelectItemsSource(nameof(ResolutionOptions))]
        public string NewBucketResolution { get; set; } = "1080p";

        [DisplayName("Audio")]
        [Description("Audio profile to match for this bucket.")]
        [SelectItemsSource(nameof(AudioOptions))]
        public string NewBucketAudio { get; set; } = "Any Audio";

        [DisplayName("Count")]
        [Description("How many streams to select from this bucket.")]
        [SelectItemsSource(nameof(CountOptions))]
        public string NewBucketCount { get; set; } = "2";

        public ButtonItem AddBucketButton { get; set; } = new ButtonItem("Add Quality Bucket")
        {
            Icon = IconNames.add,
            Data1 = AddBucketCommand,
        };

        public StatusItem BucketStatus { get; set; } = new StatusItem("Buckets", "Idle", ItemStatus.None);

        [DisplayName("Max Versions Per Item")]
        [Description("Maximum .strm versions per movie/episode. Total across all buckets should not exceed this. Default: 8.")]
        public int MaxVersionsPerItem { get; set; } = 8;

        // ═══════════════════════════════════════════════════════════════
        // Section 3: Pruning & Deduplication
        // ═══════════════════════════════════════════════════════════════

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionPruning { get; set; } = new CaptionItem("Pruning & Deduplication");

        [DisplayName("Respect user playlists & self-managed collections when pruning")]
        [Description("When enabled, items in user playlists and self-managed collections are never pruned.")]
        public bool RespectPlaylistsWhenPruning { get; set; } = true;

        [DisplayName("Auto-deduplicate against physical media in other libraries")]
        [Description("When enabled, virtual items are suppressed if the same title exists as physical media elsewhere.")]
        public bool AutoDeduplicatePhysicalMedia { get; set; } = true;

        public LabelItem PruningSummary { get; set; } = new LabelItem(
            "Items are added/removed dynamically. Playlists and self-managed collections are respected. " +
            "Physical media in other libraries is automatically deduplicated.");

        // ═══════════════════════════════════════════════════════════════
        // Section 4: Rate-Limit & Safety
        // ═══════════════════════════════════════════════════════════════

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionRateLimit { get; set; } = new CaptionItem("Rate-Limit & Safety");

        [DisplayName("Marvin Actions Per Hour")]
        [Description(
            "Limits how aggressively Marvin runs to stay a good citizen with AIOStreams and debrid providers. " +
            "Default: 360.")]
        public int MarvinActionsPerHour { get; set; } = 360;

    }
}
