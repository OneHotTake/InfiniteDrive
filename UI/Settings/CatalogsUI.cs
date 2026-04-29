using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;

namespace InfiniteDrive.UI.Settings
{
    public class CatalogsUI : EditableOptionsBase
    {
        public const string SyncNowCommand = nameof(SyncNowCommand);
        public const string ToggleCatalogCommand = nameof(ToggleCatalogCommand);

        public override string EditorTitle => "Catalogs";
        public override string EditorDescription =>
            "Catalogs are discovered automatically from your AIOStreams manifest during sync. " +
            "Toggle catalogs on/off to control which ones sync to your library.";

        public CaptionItem CaptionCatalogs { get; set; } = new CaptionItem("Catalog Sources");

        public GenericItemList CatalogList { get; set; } = new GenericItemList();

        public ButtonItem SyncNowButton { get; set; } = new ButtonItem("Sync Catalogs Now")
        {
            Icon = IconNames.sync,
            Data1 = SyncNowCommand,
        };

        public StatusItem SyncStatus { get; set; } = new StatusItem("Sync", "Idle", ItemStatus.None);

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionSync { get; set; } = new CaptionItem("Sync Settings");

        [DisplayName("Sync Interval (hours)")]
        [Description("Minimum hours between catalog syncs. Marvin runs every 10 min but will skip sync if within this interval. Default: 1.")]
        public int CatalogSyncIntervalHours { get; set; } = 1;
    }
}
