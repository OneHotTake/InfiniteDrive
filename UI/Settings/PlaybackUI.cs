using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;

namespace InfiniteDrive.UI.Settings
{
    public class PlaybackUI : EditableOptionsBase
    {
        public override string EditorTitle => "Playback";
        public override string EditorDescription =>
            "Cache and pre-load settings for stream resolution. " +
            "Quality and provider filtering is configured in your AIOStreams instance.";

        public CaptionItem CaptionCache { get; set; } = new CaptionItem("Stream Cache");

        [DisplayName("Cache Lifetime (minutes)")]
        [Description("How long a resolved stream URL is valid before re-checking. Default: 360.")]
        public int CacheLifetimeMinutes { get; set; } = 360;

        [DisplayName("API Daily Budget")]
        [Description("Maximum AIOStreams API calls per day. Default: 2000.")]
        public int ApiDailyBudget { get; set; } = 2000;

        [DisplayName("Max Concurrent Resolutions")]
        [Description("Simultaneous AIOStreams calls during background resolution. Default: 3.")]
        public int MaxConcurrentResolutions { get; set; } = 3;

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionPreCache { get; set; } = new CaptionItem("Stream Pre-Cache");

        [DisplayName("Enable Pre-Cache")]
        [Description("Background task pre-warms stream metadata so playback starts instantly.")]
        public bool EnablePreCache { get; set; } = true;

        [DisplayName("Pre-Cache Batch Size")]
        [Description("Items resolved per pre-cache run. Default: 42.")]
        public int PreCacheBatchSize { get; set; } = 42;

        [DisplayName("Pre-Cache Interval (hours)")]
        [Description("Hours between automatic pre-cache runs. Default: 6.")]
        public int PreCacheIntervalHours { get; set; } = 6;

        [DisplayName("Pre-Cache TTL (days)")]
        [Description("Days before a cached entry expires. Default: 14.")]
        public int PreCacheTTLDays { get; set; } = 14;
    }
}
