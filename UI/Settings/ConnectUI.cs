using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;

namespace InfiniteDrive.UI.Settings
{
    public class ConnectUI : EditableOptionsBase
    {
        public const string TestPrimaryCommand = nameof(TestPrimaryCommand);
        public const string TestSecondaryCommand = nameof(TestSecondaryCommand);
        public const string PreviewRecommendedCommand = nameof(PreviewRecommendedCommand);
        public const string ApplyRecommendedCommand = nameof(ApplyRecommendedCommand);

        public override string EditorTitle => "Providers";
        public override string EditorDescription => string.Empty;

        // ── Test Result (top of page) ─────────────────────────────────────────

        public CaptionItem CaptionTestResults { get; set; } = new CaptionItem("Test Results");

        public StatusItem SetupTestResult { get; set; } = new StatusItem("Status", "No tests run yet", ItemStatus.None);

        // ── Welcome ──────────────────────────────────────────────────────────

        public SpacerItem SpacerWelcome { get; set; } = new SpacerItem();
        public CaptionItem CaptionConnect { get; set; } = new CaptionItem("Connect your sources");

        public LabelItem WelcomeText { get; set; } = new LabelItem(
            "Paste one or two AIOStreams manifest URLs below. Every configured manifest is active. " +
            "Then go to the Libraries tab to tell InfiniteDrive where to save your files.");

        // ── Section 1: AIOStreams Providers ───────────────────────────────────

        public SpacerItem Spacer0 { get; set; } = new SpacerItem();
        public CaptionItem CaptionProviders { get; set; } = new CaptionItem("Manifest URLs");

        [DisplayName("Manifest 1")]
        [Description("A manifest URL from your AIOStreams web UI. Its catalogs and streams are active whenever configured.")]
        [MediaBrowser.Model.Attributes.IsPassword]
        public string PrimaryManifestUrl { get; set; } = string.Empty;

        public ButtonItem TestPrimaryButton { get; set; } = new ButtonItem("Test Manifest 1")
        {
            Icon = IconNames.network_check,
            Data1 = TestPrimaryCommand,
        };

        public LabelItem PrimaryServerUrl { get; set; } = new LabelItem("—");
        public StatusItem PrimaryUserId { get; set; } = new StatusItem("User ID", "—", ItemStatus.None);

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();

        [DisplayName("Manifest 2")]
        [Description("Optional peer manifest. Its catalogs are unioned with Manifest 1; it can also answer when an item's origin transport is unavailable.")]
        [MediaBrowser.Model.Attributes.IsPassword]
        public string SecondaryManifestUrl { get; set; } = string.Empty;

        public ButtonItem TestSecondaryButton { get; set; } = new ButtonItem("Test Manifest 2")
        {
            Icon = IconNames.network_check,
            Data1 = TestSecondaryCommand,
        };

        public LabelItem SecondaryServerUrl { get; set; } = new LabelItem("—");
        public StatusItem SecondaryUserId { get; set; } = new StatusItem("User ID", "—", ItemStatus.None);

        // ── Section 2: Recommended setup (optional, opt-in) ───────────────────

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionRecommended { get; set; } = new CaptionItem("Recommended setup (optional)");

        public LabelItem RecommendedText { get; set; } = new LabelItem(
            "InfiniteDrive works with any AIOStreams config. If you'd like, it can set a " +
            "predictable stream FORMAT and SORT order on your instances for the cleanest results. " +
            "It touches ONLY the formatter and sort order — never your catalogs, lists, providers, or keys. " +
            "Each instance has its own password (the one you set in QuackStart). Enter the password for " +
            "each instance you want updated, then Preview first. Each manifest instance needs its own password.");

        [DisplayName("Manifest 1 AIOStreams Password")]
        [Description("Password for Manifest 1. Used once, in-memory, to read and update its formatter + sort order — never saved.")]
        [MediaBrowser.Model.Attributes.IsPassword]
        public string PrimaryManifestPassword { get; set; } = string.Empty;

        [DisplayName("Manifest 2 AIOStreams Password")]
        [Description("Password for Manifest 2, if configured. Used once, in-memory, never saved.")]
        [MediaBrowser.Model.Attributes.IsPassword]
        public string SecondaryManifestPassword { get; set; } = string.Empty;

        public ButtonItem PreviewRecommendedButton { get; set; } = new ButtonItem("Preview changes")
        {
            Icon = IconNames.preview,
            Data1 = PreviewRecommendedCommand,
        };

        public ButtonItem ApplyRecommendedButton { get; set; } = new ButtonItem("Apply formatter & sort")
        {
            Icon = IconNames.done,
            Data1 = ApplyRecommendedCommand,
        };

        public StatusItem RecommendedResult { get; set; } = new StatusItem("Result", "Not run yet", ItemStatus.None);
    }
}
