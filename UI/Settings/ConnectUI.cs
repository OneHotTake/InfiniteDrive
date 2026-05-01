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
        public const string RunSetupTestCommand = nameof(RunSetupTestCommand);

        public override string EditorTitle => "Connect";
        public override string EditorDescription => string.Empty;

        // ── Welcome ──────────────────────────────────────────────────────────

        public CaptionItem CaptionConnect { get; set; } = new CaptionItem("Connect your sources");

        public LabelItem WelcomeText { get; set; } = new LabelItem(
            "Paste your AIOStreams manifest URL and Emby server address below. " +
            "Then go to the Libraries tab to tell InfiniteDrive where to save your files.");

        // ── Section 1: AIOStreams Providers ───────────────────────────────────

        public SpacerItem Spacer0 { get; set; } = new SpacerItem();
        public CaptionItem CaptionProviders { get; set; } = new CaptionItem("AIOStreams Providers");

        [DisplayName("Primary Manifest URL")]
        [Description("The main manifest URL from your AIOStreams web UI. This is where InfiniteDrive gets all its streaming links.")]
        public string PrimaryManifestUrl { get; set; } = string.Empty;

        public ButtonItem TestPrimaryButton { get; set; } = new ButtonItem("Test Primary")
        {
            Icon = IconNames.network_check,
            Data1 = TestPrimaryCommand,
        };

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();

        [DisplayName("Secondary Manifest URL (optional)")]
        [Description("A backup AIOStreams server. If your primary server goes down, InfiniteDrive will automatically use this one. Leave blank if you don't have a backup.")]
        public string SecondaryManifestUrl { get; set; } = string.Empty;

        public ButtonItem TestSecondaryButton { get; set; } = new ButtonItem("Test Secondary")
        {
            Icon = IconNames.network_check,
            Data1 = TestSecondaryCommand,
        };

        // ── Section 2: Emby Server ────────────────────────────────────────────

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionEmby { get; set; } = new CaptionItem("Emby Server");

        [DisplayName("Emby External URL")]
        [Description(
            "The address your Emby server is reachable at from other devices on your network. " +
            "This gets written into every streaming file. " +
            "Example: http://192.168.1.100:8096")]
        public string EmbyBaseUrl { get; set; } = "http://192.168.1.100:8096";

        // ── Test Result (single source of truth) ──────────────────────────────

        public SpacerItem SpacerResult { get; set; } = new SpacerItem();

        public StatusItem SetupTestResult { get; set; } = new StatusItem("Test Result", "No tests run yet", ItemStatus.None);

        public ButtonItem RunSetupTestButton { get; set; } = new ButtonItem("Run Full Setup Test")
        {
            Icon = IconNames.network_check,
            Data1 = RunSetupTestCommand,
        };
    }
}
