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

        public override string EditorTitle => "Providers";
        public override string EditorDescription => string.Empty;

        // ── Test Result (top of page) ─────────────────────────────────────────

        public CaptionItem CaptionTestResults { get; set; } = new CaptionItem("Test Results");

        public StatusItem SetupTestResult { get; set; } = new StatusItem("Status", "No tests run yet", ItemStatus.None);

        // ── Welcome ──────────────────────────────────────────────────────────

        public SpacerItem SpacerWelcome { get; set; } = new SpacerItem();
        public CaptionItem CaptionConnect { get; set; } = new CaptionItem("Connect your sources");

        public LabelItem WelcomeText { get; set; } = new LabelItem(
            "Paste your AIOStreams manifest URL and Emby server address below. " +
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

        public StatusItem PrimaryServerUrl { get; set; } = new StatusItem("Server", "—", ItemStatus.None);
        public StatusItem PrimaryUserId { get; set; } = new StatusItem("User ID", "—", ItemStatus.None);
        public LabelItem PrimaryDashboardHint { get; set; } = new LabelItem("Copy the Server URL above into your browser to open the AIOStreams web UI.");

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();

        [DisplayName("Secondary Manifest")]
        [Description("(Optional) A backup AIOStreams server. If your primary server goes down, InfiniteDrive will automatically use this one.")]
        public string SecondaryManifestUrl { get; set; } = string.Empty;

        public ButtonItem TestSecondaryButton { get; set; } = new ButtonItem("Test Secondary")
        {
            Icon = IconNames.network_check,
            Data1 = TestSecondaryCommand,
        };

        public StatusItem SecondaryServerUrl { get; set; } = new StatusItem("Server", "—", ItemStatus.None);
        public StatusItem SecondaryUserId { get; set; } = new StatusItem("User ID", "—", ItemStatus.None);
    }
}
