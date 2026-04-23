namespace InfiniteDrive.Configuration.UI.views
{
    using System.ComponentModel;

    using Emby.Web.GenericEdit;
    using Emby.Web.GenericEdit.Elements;

    using MediaBrowser.Model.Attributes;

    /// <summary>
    /// Providers tab: manifest URLs and Emby API key.
    /// Reads from PluginConfiguration on construction, writes back on save.
    /// </summary>
    public class ProvidersUI : EditableOptionsBase
    {
        public override string EditorTitle => "Providers";

        public override string EditorDescription =>
            "Configure your AIOStreams manifest URLs and Emby API key. "
            + "The primary manifest is used for all sync operations. "
            + "The secondary manifest acts as a failover if the primary is unreachable.";

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();

        public CaptionItem CaptionManifest { get; set; } = new CaptionItem("Manifest URLs");

        [DisplayName("Primary Manifest URL")]
        [Description("Full AIOStreams manifest URL including authentication parameters.")]
        public string PrimaryManifestUrl { get; set; } = string.Empty;

        [DisplayName("Secondary Manifest URL")]
        [Description("Optional fallback manifest URL used when the primary is unreachable.")]
        public string SecondaryManifestUrl { get; set; } = string.Empty;

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();

        public CaptionItem CaptionEmby { get; set; } = new CaptionItem("Emby Integration");

        [DisplayName("Emby API Key")]
        [Description("Required for Emby API access. Create one from Dashboard → API Keys.")]
        [IsPassword]
        public string EmbyApiKey { get; set; } = string.Empty;
    }
}
