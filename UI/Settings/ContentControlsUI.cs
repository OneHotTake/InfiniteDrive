using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
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
            "Quality preferences, parental controls, and blocked content management. " +
            "*Don't Panic* — Marvin is on the case.";

        // ═══════════════════════════════════════════════════════════════
        // Section 1: Quality & Resolution Preferences
        // ═══════════════════════════════════════════════════════════════

        public CaptionItem CaptionQuality { get; set; } = new CaptionItem("Quality & Resolution Preferences");

        public GenericItemList QualityTierList { get; set; } = new GenericItemList();

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();

        [DisplayName("Default Quality Tier")]
        [Description("Quality tier that plays automatically when multiple streams are available.")]
        public string DefaultQualityTier { get; set; } = "1080p (any)";

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

        public GenericItemList BlockedItemList { get; set; } = new GenericItemList();

        public ButtonItem AddToBlockListButton { get; set; } = new ButtonItem("Add to Block List")
        {
            Icon = IconNames.add,
            Data1 = AddToBlockListCommand,
        };

        public StatusItem BlockListStatus { get; set; } = new StatusItem("Block List", "Idle", ItemStatus.None);
    }
}
