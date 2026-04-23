namespace InfiniteDrive.Configuration.UI.views
{
    using System.ComponentModel;

    using Emby.Web.GenericEdit;
    using Emby.Web.GenericEdit.Elements;

    using MediaBrowser.Model.Attributes;

    /// <summary>
    /// Catalogs tab: catalog sync settings and configuration.
    /// </summary>
    public class CatalogsUI : EditableOptionsBase
    {
        public override string EditorTitle => "Catalogs";

        public override string EditorDescription =>
            "Configure which catalogs are synced from your manifest. "
            + "Catalogs define what content appears in your Emby libraries.";

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();

        public CaptionItem CaptionSettings { get; set; } = new CaptionItem("Catalog Settings");

        [DisplayName("Enable AIOStreams Catalog")]
        [Description("Enable catalog sync from the AIOStreams manifest.")]
        public bool EnableAioStreamsCatalog { get; set; } = true;

        [DisplayName("Catalog IDs")]
        [Description("Comma-separated list of specific catalog IDs to sync. Leave empty to sync all.")]
        [EditMultiline(3)]
        public string AioStreamsCatalogIds { get; set; } = string.Empty;

        [DisplayName("User Catalog Limit")]
        [Description("Maximum number of catalogs a user can enable in Discover. Default: 5.")]
        public int UserCatalogLimit { get; set; } = 5;
    }
}
