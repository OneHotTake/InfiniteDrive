namespace InfiniteDrive.Configuration.UI.views
{
    using System.ComponentModel;

    using Emby.Web.GenericEdit;
    using Emby.Web.GenericEdit.Elements;

    /// <summary>
    /// Metadata tab: language, country, TMDB settings.
    /// </summary>
    public class MetadataUI : EditableOptionsBase
    {
        public override string EditorTitle => "Metadata";

        public override string EditorDescription =>
            "Configure metadata language preferences and provider settings. "
            + "These affect how media information is fetched and displayed.";

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();

        public CaptionItem CaptionLocale { get; set; } = new CaptionItem("Language & Region");

        [DisplayName("Metadata Language")]
        [Description("Preferred language for metadata (e.g., 'en' for English).")]
        public string MetadataLanguage { get; set; } = "en";

        [DisplayName("Country Code")]
        [Description("Two-letter country code for certification lookups (e.g., 'US').")]
        public string MetadataCountryCode { get; set; } = "US";

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();

        public CaptionItem CaptionAdvanced { get; set; } = new CaptionItem("Advanced");

        [DisplayName("AIOStreams Metadata Base URL")]
        [Description("Override the default AIOStreams metadata endpoint URL. Leave empty for auto-detect.")]
        public string AioMetadataBaseUrl { get; set; } = string.Empty;

        [DisplayName("Catalog Sync Interval (hours)")]
        [Description("How often to sync catalogs from the manifest. Default: 1 hour.")]
        public int CatalogSyncIntervalHours { get; set; } = 1;

        [DisplayName("Catalog Item Cap")]
        [Description("Maximum number of items to sync per catalog. Default: 500.")]
        public int CatalogItemCap { get; set; } = 500;
    }
}
