using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;

namespace InfiniteDrive.UI.Settings
{
    public class ContentControlsUI : EditableOptionsBase
    {
        public const string AddToBlockListCommand = nameof(AddToBlockListCommand);
        public const string UnblockItemCommand = nameof(UnblockItemCommand);
        public const string AddBucketCommand = nameof(AddBucketCommand);
        public const string RemoveBucketCommand = nameof(RemoveBucketCommand);

        public override string EditorTitle => "Content Controls";
        public override string EditorDescription =>
            "Quality preferences, version buckets, parental controls, and blocked content.";

        // ═══════════════════════════════════════════════════════════════
        // Section 1: Quality & Resolution Preferences
        // ═══════════════════════════════════════════════════════════════

        public CaptionItem CaptionQuality { get; set; } = new CaptionItem("Quality & Resolution Preferences");

        [DisplayName("Use REMUX files for auto-selection")]
        [Description(
            "When enabled, 4K/1080p Bluray REMUX files (40-60GB, TrueHD/Atmos audio) are included in " +
            "auto-selection. REMUX files take 40+ seconds to probe and often require transcoding. " +
            "When disabled (recommended), REMUX is deprioritized in favor of faster-starting encodes.")]
        public bool UseRemuxForAutoSelection { get; set; } = false;

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();

        // ═══════════════════════════════════════════════════════════════
        // Section 2: Multi-Version Quality Buckets
        // ═══════════════════════════════════════════════════════════════

        public CaptionItem CaptionBuckets { get; set; } = new CaptionItem("Multi-Version Quality Buckets");

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

        // ═══════════════════════════════════════════════════════════════
        // Section 3: Parental Controls (Discover-only)
        // ═══════════════════════════════════════════════════════════════

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionParental { get; set; } = new CaptionItem("Parental Controls (Discover-only)");

        [DisplayName("Hide unrated content")]
        [Description(
            "Only affects InfiniteDrive Discover / search / browse results. " +
            "Emby native library restrictions still apply for persisted items.")]
        public bool HideUnratedContent { get; set; } = false;

        // ═══════════════════════════════════════════════════════════════
        // Section 4: Blocked Content Management
        // ═══════════════════════════════════════════════════════════════

        public SpacerItem Spacer3 { get; set; } = new SpacerItem();
        public CaptionItem CaptionBlocked { get; set; } = new CaptionItem("Blocked Content Management");

        [DisplayName("Block by Title or ID")]
        [Description("Enter a movie/show title, IMDB ID (tt1234567), TMDB ID, Kitsu ID, or AniList ID to block. Then click 'Add to Block List'.")]
        public string BlockListInput { get; set; } = string.Empty;

        public ButtonItem AddToBlockListButton { get; set; } = new ButtonItem("Add to Block List")
        {
            Icon = IconNames.add,
            Data1 = AddToBlockListCommand,
        };

        public StatusItem BlockListStatus { get; set; } = new StatusItem("Block List", "Idle", ItemStatus.None);

        public GenericItemList BlockedItemList { get; set; } = new GenericItemList();
    }
}
