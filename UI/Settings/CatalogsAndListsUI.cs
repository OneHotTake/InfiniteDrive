using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;

namespace InfiniteDrive.UI.Settings
{
    public class CatalogsAndListsUI : EditableOptionsBase
    {
        public const string SyncCatalogsCommand = nameof(SyncCatalogsCommand);
        public const string ToggleCatalogCommand = nameof(ToggleCatalogCommand);
        public const string AddSystemListCommand = nameof(AddSystemListCommand);
        public const string RemoveSystemListCommand = nameof(RemoveSystemListCommand);

        public override string EditorTitle => "Sources";
        public override string EditorDescription =>
            "AIOStreams catalogs pulled from your manifest, optional API keys for list providers, " +
            "and system-wide curated lists that supplement your Discover page.";

        // ═══════════════════════════════════════════════════════════════
        // Section 1: AIOStreams System Catalogs
        // ═══════════════════════════════════════════════════════════════

        public CaptionItem CaptionCatalogs { get; set; } = new CaptionItem("Catalogs");

        public GenericItemList CatalogList { get; set; } = new GenericItemList();

        public ButtonItem SyncCatalogsButton { get; set; } = new ButtonItem("Sync Now")
        {
            Icon = IconNames.sync,
            Data1 = SyncCatalogsCommand,
        };

        public StatusItem CatalogSyncStatus { get; set; } = new StatusItem("Sync", "Not run yet", ItemStatus.None);

        // ═══════════════════════════════════════════════════════════════
        // Section 2: List Provider API Keys
        // ═══════════════════════════════════════════════════════════════

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionProviderKeys { get; set; } = new CaptionItem("API Keys");

        [DisplayName("Trakt Client ID")]
        [Description("Optional. Required only if you want to import Trakt lists. Get a free key at trakt.tv → Settings → Your API Apps.")]
        public string TraktClientId { get; set; } = string.Empty;

        [DisplayName("TMDB API Key")]
        [Description("Optional. Required for TMDB lists and content-rating lookups in Restrictions. Get a free key at themoviedb.org → Settings → API.")]
        public string TmdbApiKey { get; set; } = string.Empty;

        // ═══════════════════════════════════════════════════════════════
        // Section 3: System-Wide Lists
        // ═══════════════════════════════════════════════════════════════

        public SpacerItem Spacer3 { get; set; } = new SpacerItem();
        public CaptionItem CaptionSystemLists { get; set; } = new CaptionItem("System Lists");

        public LabelItem SystemListsHelp { get; set; } = new LabelItem(
            "System lists are curated collections visible to all users on the Discover page. " +
            "Paste a MDBList, Trakt, TMDB, or AniList URL and InfiniteDrive will sync it automatically. " +
            "Trakt lists require a Trakt Client ID above; TMDB lists require a TMDB API key.");

        public GenericItemList SystemListTable { get; set; } = new GenericItemList();

        [DisplayName("List URL")]
        [Description("Paste a MDBList, Trakt, TMDB, or AniList URL. Syncs immediately on add.")]
        public string SystemListUrlInput { get; set; } = string.Empty;

        [DisplayName("Display Name")]
        [Description("Friendly name shown in the table above.")]
        public string SystemListNameInput { get; set; } = string.Empty;

        public ButtonItem AddSystemListButton { get; set; } = new ButtonItem("Add List")
        {
            Icon = IconNames.add,
            Data1 = AddSystemListCommand,
        };

        public StatusItem SystemListStatus { get; set; } = new StatusItem("Add status", "Ready", ItemStatus.None);

        // ═══════════════════════════════════════════════════════════════
        // Section 4: User Lists (read-only for admins)
        // ═══════════════════════════════════════════════════════════════

        public SpacerItem Spacer4 { get; set; } = new SpacerItem();
        public CaptionItem CaptionUserLists { get; set; } = new CaptionItem("User Lists");

        public LabelItem UserListsHelp { get; set; } = new LabelItem(
            "Each Emby user can create their own private lists from the Discover page. " +
            "This section shows a summary — individual lists are managed by the users themselves.");

        public GenericItemList UserListTable { get; set; } = new GenericItemList();

        public StatusItem UserListStatus { get; set; } = new StatusItem("User Lists", "—", ItemStatus.None);

        [DisplayName("Max Lists Per User")]
        [Description("How many lists each user is allowed to create. Set to 0 to disable user lists entirely.")]
        public int MaxListsPerUser { get; set; } = 10;

    }
}
