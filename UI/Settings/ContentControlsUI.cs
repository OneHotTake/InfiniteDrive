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
        public const string AddBucketCommand = nameof(AddBucketCommand);
        public const string RemoveBucketCommand = nameof(RemoveBucketCommand);

        public override string EditorTitle => "Quality";
        public override string EditorDescription =>
            "Quality tiers Marvin uses when selecting streams. Each bucket is a resolution + audio profile with a count.";

        // ═══════════════════════════════════════════════════════════════
        // Section 1: Quality & Resolution Preferences
        // ═══════════════════════════════════════════════════════════════

        public CaptionItem CaptionQuality { get; set; } = new CaptionItem("Quality");

        [DisplayName("Use REMUX files for auto-selection")]
        [Description("Includes 40–60 GB REMUX files in auto-selection. Not recommended — slow to start and often requires transcoding.")]
        public bool UseRemuxForAutoSelection { get; set; } = false;

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();

        // ═══════════════════════════════════════════════════════════════
        // Section 2: Multi-Version Quality Buckets
        // ═══════════════════════════════════════════════════════════════

        public CaptionItem CaptionBuckets { get; set; } = new CaptionItem("Quality Buckets");

        public LabelItem BucketHelp { get; set; } = new LabelItem(
            "Each bucket defines a quality tier. Marvin picks the best matching stream per bucket. Drag to reorder priority.");

        public GenericItemList BucketList { get; set; } = new GenericItemList();

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> ResolutionOptions { get; set; } = new List<EditorSelectOption>
        {
            new() { Value = "4K",    Name = "4K",    IsEnabled = true },
            new() { Value = "1440p", Name = "1440p", IsEnabled = true },
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

        public ButtonItem AddBucketButton { get; set; } = new ButtonItem("Add Bucket")
        {
            Icon = IconNames.add,
            Data1 = AddBucketCommand,
        };

        public StatusItem BucketStatus { get; set; } = new StatusItem("Buckets", "—", ItemStatus.None);

    }
}
