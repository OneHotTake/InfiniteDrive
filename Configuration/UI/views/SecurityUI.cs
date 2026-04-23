namespace InfiniteDrive.Configuration.UI.views
{
    using System.ComponentModel;

    using Emby.Web.GenericEdit;
    using Emby.Web.GenericEdit.Elements;

    using MediaBrowser.Model.Attributes;

    /// <summary>
    /// Security tab: API keys, plugin secret status.
    /// </summary>
    public class SecurityUI : EditableOptionsBase
    {
        public override string EditorTitle => "Security";

        public override string EditorDescription =>
            "Manage API keys and security settings. "
            + "The PluginSecret is used to sign stream URLs. "
            + "The Emby API key is required for library management.";

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();

        public CaptionItem CaptionKeys { get; set; } = new CaptionItem("API Keys");

        [DisplayName("Emby API Key")]
        [Description("Required for Emby REST API access (library creation, item management). "
                     + "Create from Dashboard → API Keys.")]
        [IsPassword]
        public string EmbyApiKey { get; set; } = string.Empty;

        [DisplayName("TMDB API Key")]
        [Description("Optional. Used for metadata enrichment and certification lookup.")]
        [IsPassword]
        public string TmdbApiKey { get; set; } = string.Empty;

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();

        public CaptionItem CaptionSecret { get; set; } = new CaptionItem("Stream Signing");

        [DisplayName("Plugin Secret Status")]
        [Description("The PluginSecret is auto-generated and used to sign .strm URLs. "
                     + "Rotating invalidates all existing stream URLs.")]
        public string PluginSecretStatus { get; set; } = "Not initialized";

        [DisplayName("Last Rotation")]
        [Description("Timestamp of the last PluginSecret rotation.")]
        public string LastRotationInfo { get; set; } = "Never";
    }
}
