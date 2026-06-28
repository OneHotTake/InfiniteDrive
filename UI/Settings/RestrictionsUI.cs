using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;

namespace InfiniteDrive.UI.Settings
{
    public class RestrictionsUI : EditableOptionsBase
    {
        public const string AddToBlockListCommand = nameof(AddToBlockListCommand);
        public const string UnblockItemCommand = nameof(UnblockItemCommand);

        public override string EditorTitle => "Restrictions";
        public override string EditorDescription =>
            "Content filtering — hide unrated titles, block specific content by title or provider ID.";

        // ── Content Rating ────────────────────────────────────────────────────

        public CaptionItem CaptionRating { get; set; } = new CaptionItem("Content Rating");

        [DisplayName("Hide unrated content")]
        [Description("Applies to InfiniteDrive Discover results only. Does not affect your Emby library.")]
        public bool HideUnratedContent { get; set; } = false;

        // ── Blocked Content ───────────────────────────────────────────────────

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionBlocked { get; set; } = new CaptionItem("Blocked Content");

        [DisplayName("Block by Title or ID")]
        [Description("Title, IMDB ID (tt1234567), TMDB, Kitsu, or AniList ID.")]
        public string BlockListInput { get; set; } = string.Empty;

        public ButtonItem AddToBlockListButton { get; set; } = new ButtonItem("Block")
        {
            Icon = IconNames.add,
            Data1 = AddToBlockListCommand,
        };

        public StatusItem BlockListStatus { get; set; } = new StatusItem("Block List", "Nothing blocked", ItemStatus.None);

        public GenericItemList BlockedItemList { get; set; } = new GenericItemList();
    }
}
