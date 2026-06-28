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
            "Paste your AIOStreams manifest URL below and click Test Primary to verify it. " +
            "Then go to the Libraries tab to tell InfiniteDrive where to save your files.");

        // ── Section 1: AIOStreams Providers ───────────────────────────────────

        public SpacerItem Spacer0 { get; set; } = new SpacerItem();
        public CaptionItem CaptionProviders { get; set; } = new CaptionItem("Manifest URLs");

        [DisplayName("Primary Manifest")]
        [Description("The main manifest URL from your AIOStreams web UI. This is where InfiniteDrive gets all its streaming links.")]
        public string PrimaryManifestUrl { get; set; } = string.Empty;

        public ButtonItem TestPrimaryButton { get; set; } = new ButtonItem("Test Primary")
        {
            Icon = IconNames.network_check,
            Data1 = TestPrimaryCommand,
        };

        public LabelItem PrimaryServerUrl { get; set; } = new LabelItem("—");
        public StatusItem PrimaryUserId { get; set; } = new StatusItem("User ID", "—", ItemStatus.None);

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();

        [DisplayName("Secondary Manifest")]
        [Description("(Optional) A backup AIOStreams server. If your primary server goes down, InfiniteDrive will automatically use this one.")]
        public string SecondaryManifestUrl { get; set; } = string.Empty;

        public ButtonItem TestSecondaryButton { get; set; } = new ButtonItem("Test Secondary")
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
            "predictable stream FORMAT and SORT order on your primary instance for the cleanest results. " +
            "It touches ONLY the formatter and sort order — never your catalogs, lists, providers, or keys. " +
            "Enter your AIOStreams password (the one you set in QuackStart) and Preview first.");

        [DisplayName("AIOStreams Password")]
        [Description("Password for your primary AIOStreams instance. Used only to read and update the formatter + sort order. Stored locally in plugin config.")]
        [MediaBrowser.Model.Attributes.IsPassword]
        public string PrimaryManifestPassword { get; set; } = string.Empty;

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
