using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;

namespace InfiniteDrive.UI.Discover
{
    public class DiscoverUI : EditableOptionsBase
    {
        public const string SearchCommand = nameof(SearchCommand);
        public const string AddToLibraryCommand = nameof(AddToLibraryCommand);
        public const string UnblockCommand = nameof(UnblockCommand);

        public override string EditorTitle => "Discover";
        public override string EditorDescription => "Search for movies and TV shows to add to your library.";

        // ── Search ───────────────────────────────────────────────────────────────

        public CaptionItem CaptionSearch { get; set; } = new CaptionItem("Search");

        [DisplayName("Search")]
        [Description("Enter a title and press Search.")]
        public string SearchQuery { get; set; } = string.Empty;

        public ButtonItem SearchButton { get; set; } = new ButtonItem("Search")
        {
            Icon = IconNames.search,
            Data1 = SearchCommand,
        };

        [DisplayName("Results")]
        public GenericItemList SearchResults { get; set; } = new GenericItemList();

        // ── Popular ──────────────────────────────────────────────────────────────

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionPopular { get; set; } = new CaptionItem("Popular Movies");

        [DisplayName("Popular Movies")]
        public GenericItemList PopularMovies { get; set; } = new GenericItemList();

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionPopularSeries { get; set; } = new CaptionItem("Popular TV Shows");

        [DisplayName("Popular TV Shows")]
        public GenericItemList PopularSeries { get; set; } = new GenericItemList();

        // ── Blocked Items ────────────────────────────────────────────────────────

        public SpacerItem Spacer3 { get; set; } = new SpacerItem();
        public CaptionItem CaptionBlocked { get; set; } = new CaptionItem("Blocked Items");

        [DisplayName("Blocked Items")]
        [Description("Items blocked from syncing. Use the Unblock button to restore.")]
        public GenericItemList BlockedItems { get; set; } = new GenericItemList();
    }
}
