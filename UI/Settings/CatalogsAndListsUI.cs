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

        public override string EditorTitle => "Catalogs & Lists";
        public override string EditorDescription =>
            "Manage AIOStreams system catalogs, list provider API keys, system-wide lists, and user lists.";

        // ═══════════════════════════════════════════════════════════════
        // Section 1: AIOStreams System Catalogs
        // ═══════════════════════════════════════════════════════════════

        public CaptionItem CaptionCatalogs { get; set; } = new CaptionItem("AIOStreams System Catalogs");

        public GenericItemList CatalogList { get; set; } = new GenericItemList();

        public ButtonItem SyncCatalogsButton { get; set; } = new ButtonItem("Refresh All Catalogs Now")
        {
            Icon = IconNames.sync,
            Data1 = SyncCatalogsCommand,
        };

        public StatusItem CatalogSyncStatus { get; set; } = new StatusItem("Sync", "Idle", ItemStatus.None);

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionCatalogSettings { get; set; } = new CaptionItem("Catalog Sync Settings");

        [DisplayName("Catalog Sync Interval (hours)")]
        [Description("Minimum hours between catalog syncs. Marvin runs every 10 min but will skip sync if within this interval. Default: 1.")]
        public int CatalogSyncIntervalHours { get; set; } = 1;

        // ═══════════════════════════════════════════════════════════════
        // Section 2: List Provider API Keys
        // ═══════════════════════════════════════════════════════════════

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionProviderKeys { get; set; } = new CaptionItem("List Provider API Keys");

        [DisplayName("Trakt Client ID")]
        [Description("Used for Trakt list provider. Required for Trakt lists.")]
        public string TraktClientId { get; set; } = string.Empty;

        [DisplayName("TMDB API Key")]
        [Description("Used for TMDB list provider and certification lookup. Required for TMDB lists and parental controls.")]
        public string TmdbApiKey { get; set; } = string.Empty;

        // ═══════════════════════════════════════════════════════════════
        // Section 3: System-Wide Lists
        // ═══════════════════════════════════════════════════════════════

        public SpacerItem Spacer3 { get; set; } = new SpacerItem();
        public CaptionItem CaptionSystemLists { get; set; } = new CaptionItem("System-Wide Lists");

        public GenericItemList SystemListTable { get; set; } = new GenericItemList();

        public ButtonItem AddSystemListButton { get; set; } = new ButtonItem("Add New System List")
        {
            Icon = IconNames.add,
            Data1 = AddSystemListCommand,
        };

        public StatusItem SystemListStatus { get; set; } = new StatusItem("System Lists", "Idle", ItemStatus.None);

        // ═══════════════════════════════════════════════════════════════
        // Section 4: User Lists (read-only for admins)
        // ═══════════════════════════════════════════════════════════════

        public SpacerItem Spacer4 { get; set; } = new SpacerItem();
        public CaptionItem CaptionUserLists { get; set; } = new CaptionItem("User Lists (Read-Only)");

        public GenericItemList UserListTable { get; set; } = new GenericItemList();

        public StatusItem UserListStatus { get; set; } = new StatusItem("User Lists", "Idle", ItemStatus.None);

        [DisplayName("Max Lists Per User")]
        [Description("Maximum number of lists each user can create. Each user list automatically creates a native Emby playlist. Set to 0 to disable user lists entirely.")]
        public int MaxListsPerUser { get; set; } = 10;

        // ── Footer ────────────────────────────────────────────────────────────

        public SpacerItem FooterSpacer { get; set; } = new SpacerItem();
        public LabelItem Footer { get; set; } = new LabelItem("*Don't Panic* — Marvin is on the case.");
    }
}
