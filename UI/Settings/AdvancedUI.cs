using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;

namespace InfiniteDrive.UI.Settings
{
    /// <summary>
    /// Merged advanced settings: language, rate limits, security, series fallbacks, edge cases.
    /// Description intentionally says "you don't need to touch this."
    /// </summary>
    public class AdvancedUI : EditableOptionsBase
    {
        public const string RotateSecretCommand = nameof(RotateSecretCommand);

        public override string EditorTitle => "Advanced";
        public override string EditorDescription =>
            "These settings have sensible defaults. You don't need to change them unless you have a specific reason.";

        // ── Skip / Filters ──────────────────────────────────────────────────

        [DisplayName("Skip Unaired Episodes")]
        [Description("Don't write .strm files for episodes that haven't aired yet.")]
        public bool SkipFutureEpisodes { get; set; } = true;

        // ── Rate Limits ───────────────────────────────────────────────────────

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionRateLimits { get; set; } = new CaptionItem("Rate Limits");

        [DisplayName("API Daily Budget")]
        [Description("Maximum AIOStreams API calls per day. Default: 2000.")]
        public int ApiDailyBudget { get; set; } = 2000;

        [DisplayName("Stream Cache Lifetime (minutes)")]
        [Description("How long a resolved CDN URL is valid before re-checking. Default: 360.")]
        public int CacheLifetimeMinutes { get; set; } = 360;

        // ── Security ─────────────────────────────────────────────────────────

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionSecurity { get; set; } = new CaptionItem("Security");

        public LabelItem SecurityInfo { get; set; } = new LabelItem(
            "The plugin signs .strm URLs with an HMAC-SHA256 secret — auto-generated on first run. " +
            "Rotating it invalidates all existing .strm files; a sync will regenerate them.");

        [DisplayName("Signature Validity (days)")]
        [Description("Days signed .strm URLs remain valid before expiring. Default: 365.")]
        public int SignatureValidityDays { get; set; } = 365;

        [DisplayName("Plugin Secret")]
        [Description("Auto-generated signing secret. Do not share this value.")]
        [IsPassword]
        public string PluginSecret { get; set; } = string.Empty;

        public ButtonItem RotateSecretButton { get; set; } = new ButtonItem("Rotate Secret")
        {
            Icon = IconNames.vpn_key,
            Data1 = RotateSecretCommand,
            ConfirmationPrompt = "Rotating the secret invalidates all existing .strm files. The next catalog sync will regenerate them. Continue?"
        };

        // ── Series Fallback Defaults ──────────────────────────────────────────

        public SpacerItem Spacer3 { get; set; } = new SpacerItem();
        public CaptionItem CaptionSeriesDefaults { get; set; } = new CaptionItem("Series Fallback Defaults");

        public LabelItem SeriesDefaultsInfo { get; set; } = new LabelItem(
            "Used only when AIOStreams returns incomplete series metadata (rare).");

        [DisplayName("Default Seasons")]
        [Description("Seasons to write when metadata is unavailable. Default: 1.")]
        public int DefaultSeriesSeasons { get; set; } = 1;

        [DisplayName("Default Episodes Per Season")]
        [Description("Episodes per season when metadata is unavailable. Default: 10.")]
        public int DefaultSeriesEpisodesPerSeason { get; set; } = 10;

        // ── Other ─────────────────────────────────────────────────────────────

        public SpacerItem Spacer4 { get; set; } = new SpacerItem();
        public CaptionItem CaptionOther { get; set; } = new CaptionItem("Other");

        [DisplayName("Suppress Outage Banners")]
        [Description("Hide timeout/outage UI banners during provider downtime.")]
        public bool DontPanic { get; set; } = false;

        [DisplayName("Max Proxy Streams")]
        [Description("Max simultaneous proxied streams before redirecting. Default: 5.")]
        public int MaxConcurrentProxyStreams { get; set; } = 5;
    }
}
