using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;

namespace InfiniteDrive.UI
{
    public class SourcesUI : EditableOptionsBase
    {
        public override string EditorTitle => "Sources";

        [DisplayName("Enable AIOStreams Catalogs")]
        [Description("Fetch catalog items from AIOStreams catalog endpoints. Reads manifest to discover catalogs automatically.")]
        public bool EnableAioStreamsCatalog { get; set; } = true;

        [DisplayName("Catalog Filter IDs")]
        [Description("Optional comma-separated catalog IDs to sync. Leave empty to sync all catalogs from the manifest.")]
        public string AioStreamsCatalogIds { get; set; } = string.Empty;

        [DisplayName("Item Cap")]
        [Description("Maximum items fetched from any single catalog per sync. Default: 500.")]
        public int CatalogItemCap { get; set; } = 500;

        [DisplayName("Sync Interval (hours)")]
        [Description("Minimum hours between successful syncs for a single source. Default: 24.")]
        public int CatalogSyncIntervalHours { get; set; } = 24;

        [DisplayName("Auto-add Cinemeta")]
        [Description("Automatically add Cinemeta (Top Movies/Series) when AIOStreams has no catalogs.")]
        public bool EnableCinemetaDefault { get; set; } = true;

        [DisplayName("Cache Lifetime (minutes)")]
        [Description("How long resolved stream URLs remain valid before re-validation. Default: 360.")]
        public int CacheLifetimeMinutes { get; set; } = 360;

        [DisplayName("API Daily Budget")]
        [Description("Maximum AIOStreams API calls per calendar day (UTC). Default: 2000.")]
        public int ApiDailyBudget { get; set; } = 2000;

        [DisplayName("Concurrent Resolutions")]
        [Description("Max simultaneous AIOStreams HTTP calls during pre-resolution. Default: 3.")]
        public int MaxConcurrentResolutions { get; set; } = 3;

        [DisplayName("Resolve Timeout (seconds)")]
        [Description("Timeout for on-demand AIOStreams resolution. Default: 30.")]
        public int SyncResolveTimeoutSeconds { get; set; } = 30;

        [DisplayName("Proxy Mode")]
        [Description("How streams are served: auto (recommended), redirect (302), or proxy (passthrough for some TVs).")]
        public string ProxyMode { get; set; } = "auto";

        [DisplayName("Max Proxy Streams")]
        [Description("Max simultaneous proxy streams before fallback to redirect. Default: 5.")]
        public int MaxConcurrentProxyStreams { get; set; } = 5;

        [DisplayName("Candidates Per Provider")]
        [Description("Stream candidates stored per debrid provider per item. Default: 3.")]
        public int CandidatesPerProvider { get; set; } = 3;

        [DisplayName("Candidate TTL (hours)")]
        [Description("How long normalized candidates remain valid before expiry. Default: 6.")]
        public int CandidateTtlHours { get; set; } = 6;

        [DisplayName("Next-Up Lookahead")]
        [Description("Episodes to pre-resolve after playback stops. Default: 2. Set 0 to disable.")]
        public int NextUpLookaheadEpisodes { get; set; } = 2;

        [DisplayName("Refresh Catalogs")]
        public ButtonItem RefreshButton => new ButtonItem
        {
            Caption = "Sync Catalogs Now",
            CommandId = "trigger-sync"
        };

        public SourcesUI() { }

        public SourcesUI(PluginConfiguration cfg)
        {
            EnableAioStreamsCatalog = cfg.EnableAioStreamsCatalog;
            AioStreamsCatalogIds = cfg.AioStreamsCatalogIds;
            CatalogItemCap = cfg.CatalogItemCap;
            CatalogSyncIntervalHours = cfg.CatalogSyncIntervalHours;
            EnableCinemetaDefault = cfg.EnableCinemetaDefault;
            CacheLifetimeMinutes = cfg.CacheLifetimeMinutes;
            ApiDailyBudget = cfg.ApiDailyBudget;
            MaxConcurrentResolutions = cfg.MaxConcurrentResolutions;
            SyncResolveTimeoutSeconds = cfg.SyncResolveTimeoutSeconds;
            ProxyMode = cfg.ProxyMode;
            MaxConcurrentProxyStreams = cfg.MaxConcurrentProxyStreams;
            CandidatesPerProvider = cfg.CandidatesPerProvider;
            CandidateTtlHours = cfg.CandidateTtlHours;
            NextUpLookaheadEpisodes = cfg.NextUpLookaheadEpisodes;
        }

        public void ApplyTo(PluginConfiguration cfg)
        {
            cfg.EnableAioStreamsCatalog = EnableAioStreamsCatalog;
            cfg.AioStreamsCatalogIds = AioStreamsCatalogIds;
            cfg.CatalogItemCap = CatalogItemCap;
            cfg.CatalogSyncIntervalHours = CatalogSyncIntervalHours;
            cfg.EnableCinemetaDefault = EnableCinemetaDefault;
            cfg.CacheLifetimeMinutes = CacheLifetimeMinutes;
            cfg.ApiDailyBudget = ApiDailyBudget;
            cfg.MaxConcurrentResolutions = MaxConcurrentResolutions;
            cfg.SyncResolveTimeoutSeconds = SyncResolveTimeoutSeconds;
            cfg.ProxyMode = ProxyMode;
            cfg.MaxConcurrentProxyStreams = MaxConcurrentProxyStreams;
            cfg.CandidatesPerProvider = CandidatesPerProvider;
            cfg.CandidateTtlHours = CandidateTtlHours;
            cfg.NextUpLookaheadEpisodes = NextUpLookaheadEpisodes;
        }
    }
}
