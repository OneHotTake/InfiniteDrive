using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;

namespace InfiniteDrive.UI
{
    public class ProvidersUI : EditableOptionsBase
    {
        public override string EditorTitle => "Providers";

        [DisplayName("Primary AIOStreams URL")]
        [Description("Full manifest URL from your AIOStreams web UI. The plugin extracts base URL, UUID, and token automatically.")]
        public string PrimaryManifestUrl { get; set; } = string.Empty;

        [DisplayName("Test Connection")]
        public ButtonItem TestConnectionButton => new ButtonItem
        {
            Caption = "Test Connection",
            CommandId = "test-connection"
        };

        [DisplayName("Connection Status")]
        public StatusItem ConnectionStatus { get; set; } = new StatusItem
        {
            Caption = "AIOStreams",
            StatusText = "Not tested"
        };

        [DisplayName("Backup AIOStreams URL")]
        [Description("Optional secondary AIOStreams instance for failover. Leave empty if not needed.")]
        public string SecondaryManifestUrl { get; set; } = string.Empty;

        [DisplayName("Enable Backup")]
        [Description("Use secondary URL as fallback when primary is unreachable.")]
        public bool EnableBackupAioStreams { get; set; }

        [DisplayName("AIOMetadata Base URL")]
        [Description("Optional metadata enrichment API. Format: https://<instance>/meta/{type}/{id}.json")]
        public string AioMetadataBaseUrl { get; set; } = string.Empty;

        [DisplayName("Accepted Stream Types")]
        [Description("Comma-separated: debrid,torrent,usenet,http,live. Default: debrid.")]
        public string AcceptedStreamTypes { get; set; } = "debrid";

        [DisplayName("Provider Priority")]
        [Description("Comma-separated provider IDs in priority order. E.g. realdebrid,torbox,alldebrid.")]
        public string ProviderPriorityOrder { get; set; } = "realdebrid,torbox,alldebrid,debridlink,premiumize,stremthru,usenet,http";

        [DisplayName("Instance Info")]
        public StatusItem InstanceInfo { get; set; } = new StatusItem
        {
            Caption = "Instance",
            StatusText = "Not connected"
        };

        public ProvidersUI() { }

        public ProvidersUI(PluginConfiguration cfg)
        {
            PrimaryManifestUrl = cfg.PrimaryManifestUrl;
            SecondaryManifestUrl = cfg.SecondaryManifestUrl;
            EnableBackupAioStreams = cfg.EnableBackupAioStreams;
            AioMetadataBaseUrl = cfg.AioMetadataBaseUrl;
            AcceptedStreamTypes = cfg.AioStreamsAcceptedStreamTypes;
            ProviderPriorityOrder = cfg.ProviderPriorityOrder;

            if (!string.IsNullOrEmpty(cfg.AioStreamsDiscoveredName))
                InstanceInfo = new StatusItem
                {
                    Caption = "Instance",
                    StatusText = $"{cfg.AioStreamsDiscoveredName} v{cfg.AioStreamsDiscoveredVersion}"
                };
        }

        public void ApplyTo(PluginConfiguration cfg)
        {
            cfg.PrimaryManifestUrl = PrimaryManifestUrl;
            cfg.SecondaryManifestUrl = SecondaryManifestUrl;
            cfg.EnableBackupAioStreams = EnableBackupAioStreams;
            cfg.AioMetadataBaseUrl = AioMetadataBaseUrl;
            cfg.AioStreamsAcceptedStreamTypes = AcceptedStreamTypes;
            cfg.ProviderPriorityOrder = ProviderPriorityOrder;
        }
    }
}
