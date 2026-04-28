using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;

namespace InfiniteDrive.UI.Settings
{
    public class CatalogsUI : EditableOptionsBase
    {
        public const string ToggleCatalogCommand = nameof(ToggleCatalogCommand);
        public const string RefreshCatalogsCommand = nameof(RefreshCatalogsCommand);

        public override string EditorTitle => "Catalogs";
        public override string EditorDescription =>
            "Catalogs are discovered automatically from your AIOStreams manifest during sync. " +
            "Toggle individual catalogs on or off, or adjust the sync settings below.";

        public CaptionItem CaptionCatalogs { get; set; } = new CaptionItem("Catalog Sources");

        [DisplayName("Catalogs")]
        [Description("Sources fetched from AIOStreams. Toggle to enable/disable individual catalogs.")]
        public GenericItemList CatalogList { get; set; } = new GenericItemList();

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionSync { get; set; } = new CaptionItem("Sync Settings");

        [DisplayName("Max Items Per Catalog")]
        [Description("Maximum items fetched per catalog per sync. Default: 500.")]
        public int CatalogItemCap { get; set; } = 500;

        [DisplayName("Sync Interval (hours)")]
        [Description("Minimum hours between catalog syncs. Default: 1.")]
        public int CatalogSyncIntervalHours { get; set; } = 1;
    }
}
