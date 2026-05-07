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
            "AIOStreams catalogs, list provider keys, and shared lists.";

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

        public StatusItem CatalogSyncStatus { get; set; } = new StatusItem("Sync", "—", ItemStatus.None);

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionCatalogSettings { get; set; } = new CaptionItem("Sync Settings");

        [DisplayName("Sync interval (hours)")]
        [Description("Minimum hours between catalog syncs. Marvin runs every 10 min but will skip sync if within this interval. Default: 1.")]
        public int CatalogSyncIntervalHours { get; set; } = 1;

        // ═══════════════════════════════════════════════════════════════
        // Section 2: List Provider API Keys
        // ═══════════════════════════════════════════════════════════════

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionProviderKeys { get; set; } = new CaptionItem("API Keys");

        [DisplayName("Trakt Client ID")]
        [Description("Required for Trakt lists.")]
        public string TraktClientId { get; set; } = string.Empty;

        [DisplayName("TMDB API Key")]
        [Description("Required for TMDB lists and parental controls.")]
        public string TmdbApiKey { get; set; } = string.Empty;

        // ═══════════════════════════════════════════════════════════════
        // Section 3: System-Wide Lists
        // ═══════════════════════════════════════════════════════════════

        public SpacerItem Spacer3 { get; set; } = new SpacerItem();
        public CaptionItem CaptionSystemLists { get; set; } = new CaptionItem("System Lists");

        public GenericItemList SystemListTable { get; set; } = new GenericItemList();

        [DisplayName("List URL")]
        [Description("Trakt, TMDB, MDBList, or AniList list URL.")]
        public string SystemListUrlInput { get; set; } = string.Empty;

        [DisplayName("Display Name")]
        [Description("Friendly name shown in the table above.")]
        public string SystemListNameInput { get; set; } = string.Empty;

        public ButtonItem AddSystemListButton { get; set; } = new ButtonItem("Add List")
        {
            Icon = IconNames.add,
            Data1 = AddSystemListCommand,
        };

        public StatusItem SystemListStatus { get; set; } = new StatusItem("System Lists", "—", ItemStatus.None);

        // ═══════════════════════════════════════════════════════════════
        // Section 4: User Lists (read-only for admins)
        // ═══════════════════════════════════════════════════════════════

        public SpacerItem Spacer4 { get; set; } = new SpacerItem();
        public CaptionItem CaptionUserLists { get; set; } = new CaptionItem("User Lists");

        public GenericItemList UserListTable { get; set; } = new GenericItemList();

        public StatusItem UserListStatus { get; set; } = new StatusItem("User Lists", "—", ItemStatus.None);

        [DisplayName("Max Lists Per User")]
        [Description("Maximum lists each user can create. 0 disables user lists.")]
        public int MaxListsPerUser { get; set; } = 10;

    }
}
