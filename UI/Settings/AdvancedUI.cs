using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;

namespace InfiniteDrive.UI.Settings
{
    public class AdvancedUI : EditableOptionsBase
    {
        public override string EditorTitle => "Advanced";
        public override string EditorDescription => "Advanced settings for debugging and fine-tuning.";

        public CaptionItem CaptionBehavior { get; set; } = new CaptionItem("Behavior");

        [DisplayName("Suppress Outage Banners")]
        [Description("Hide panic/timeout UI banners during provider outages.")]
        public bool DontPanic { get; set; } = false;

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionDefaults { get; set; } = new CaptionItem("Series Defaults");

        [DisplayName("Default Series Seasons")]
        [Description("Seasons to write when series metadata is unavailable. Default: 1.")]
        public int DefaultSeriesSeasons { get; set; } = 1;

        [DisplayName("Default Episodes Per Season")]
        [Description("Episodes per season to write when metadata is unavailable. Default: 10.")]
        public int DefaultSeriesEpisodesPerSeason { get; set; } = 10;

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionProxy { get; set; } = new CaptionItem("Proxy");

        [DisplayName("Max Concurrent Proxy Streams")]
        [Description("Max simultaneous proxied streams before falling back to redirect. Default: 5.")]
        public int MaxConcurrentProxyStreams { get; set; } = 5;
    }
}
