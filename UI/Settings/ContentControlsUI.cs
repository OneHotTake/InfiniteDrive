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
        public const string ToggleTierCommand = nameof(ToggleTierCommand);
        public const string AddToBlockListCommand = nameof(AddToBlockListCommand);
        public const string UnblockItemCommand = nameof(UnblockItemCommand);

        public override string EditorTitle => "Content Controls";
        public override string EditorDescription =>
            "Quality preferences, parental controls, and blocked content management.";

        // ═══════════════════════════════════════════════════════════════
        // Section 1: Quality & Resolution Preferences
        // ═══════════════════════════════════════════════════════════════

        public CaptionItem CaptionQuality { get; set; } = new CaptionItem("Quality & Resolution Preferences");

        public GenericItemList QualityTierList { get; set; } = new GenericItemList();

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();

        // Options for the Default Quality Tier dropdown
        [Browsable(false)]
        public List<EditorSelectOption> DefaultQualityTierOptions { get; set; } = new List<EditorSelectOption>
        {
            new() { Value = "4K REMUX / HDR / Atmos", Name = "4K REMUX / HDR / Atmos" },
            new() { Value = "4K 5.1 / DTS",          Name = "4K 5.1 / DTS" },
            new() { Value = "4K (any)",              Name = "4K (any)" },
            new() { Value = "1080p Atmos / TrueHD",  Name = "1080p Atmos / TrueHD" },
            new() { Value = "1080p 5.1",             Name = "1080p 5.1" },
            new() { Value = "1080p (any)",           Name = "1080p (any)" },
            new() { Value = "720p",                  Name = "720p" },
            new() { Value = "SD / Unknown / Low-bandwidth", Name = "SD / Unknown / Low-bandwidth" },
        };

        [DisplayName("Default Quality Tier")]
        [Description("Quality tier that plays automatically when multiple streams are available.")]
        [SelectItemsSource(nameof(DefaultQualityTierOptions))]
        public string DefaultQualityTier { get; set; } = "1080p (any)";

        public SpacerItem Spacer1_5 { get; set; } = new SpacerItem();

        [DisplayName("Use REMUX files for auto-selection")]
        [Description(
            "When enabled, 4K/1080p Bluray REMUX files (40-60GB, TrueHD/Atmos audio) are included in " +
            "auto-selection. REMUX files take 40+ seconds to probe and often require transcoding. " +
            "When disabled (recommended), REMUX is deprioritized in favor of faster-starting encodes.")]
        public bool UseRemuxForAutoSelection { get; set; } = false;

        public SpacerItem Spacer1_75 { get; set; } = new SpacerItem();

        public LabelItem RemuxWarning { get; set; } = new LabelItem
        (
            "⚠️ ATTENTION: Please review your AIOStreams configuration. InfiniteDrive will attempt " +
            "to play the highest quality available for your chosen setup. If your provider offers " +
            "mostly REMUX files, consider enabling the 'Use REMUX files' toggle above, but expect " +
            "30-60 second startup times."
        );

        // ═══════════════════════════════════════════════════════════════
        // Section 2: Parental Controls (Discover-only)
        // ═══════════════════════════════════════════════════════════════

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionParental { get; set; } = new CaptionItem("Parental Controls (Discover-only)");

        [DisplayName("Hide unrated content")]
        [Description(
            "Only affects InfiniteDrive Discover / search / browse results. " +
            "Emby native library restrictions still apply for persisted items.")]
        public bool HideUnratedContent { get; set; } = false;

        // ═══════════════════════════════════════════════════════════════
        // Section 3: Blocked Content Management
        // ═══════════════════════════════════════════════════════════════

        public SpacerItem Spacer3 { get; set; } = new SpacerItem();
        public CaptionItem CaptionBlocked { get; set; } = new CaptionItem("Blocked Content Management");

        [DisplayName("Block by Title or ID")]
        [Description("Enter a movie/show title, IMDB ID (tt1234567), or TMDB ID to block. Then click 'Add to Block List'.")]
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
