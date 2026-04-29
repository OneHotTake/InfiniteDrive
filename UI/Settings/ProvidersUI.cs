using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;

// Note: StatusItem and ItemStatus are in Emby.Web.GenericEdit.Elements.List

namespace InfiniteDrive.UI.Settings
{
    public class ProvidersUI : EditableOptionsBase
    {
        public const string TestPrimaryCommand = nameof(TestPrimaryCommand);
        public const string TestSecondaryCommand = nameof(TestSecondaryCommand);

        public override string EditorTitle => "Connect";
        public override string EditorDescription =>
            "Paste your AIOStreams manifest URL below — the same URL you'd paste into Stremio. " +
            "If you have a backup AIOStreams instance, add it as the secondary URL.";

        public CaptionItem CaptionGettingStarted { get; set; } = new CaptionItem("Getting Started");

        public LabelItem GettingStartedLabel { get; set; } = new LabelItem(
            "1. Open your AIOStreams web UI\n" +
            "2. Copy the manifest URL (looks like: http://host:port/stremio/manifest.json)\n" +
            "3. Paste it into the Primary URL field below\n" +
            "4. Click Test to verify the connection");

        public SpacerItem Spacer0 { get; set; } = new SpacerItem();
        public CaptionItem CaptionPrimary { get; set; } = new CaptionItem("Primary AIOStreams");

        [DisplayName("Primary Manifest URL")]
        [Description("Full manifest URL from your AIOStreams web UI (include auth token if required).")]
        public string PrimaryManifestUrl { get; set; } = string.Empty;

        public StatusItem PrimaryStatus { get; set; } = new StatusItem("Connection", "Not tested", ItemStatus.Unknown);

        public ButtonItem TestPrimaryButton { get; set; } = new ButtonItem("Test Connection")
        {
            Icon = IconNames.network_check,
            Data1 = TestPrimaryCommand,
        };

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionSecondary { get; set; } = new CaptionItem("Backup AIOStreams (optional)");

        [DisplayName("Secondary Manifest URL")]
        [Description("If this is set, it's used automatically when the primary is unreachable.")]
        public string SecondaryManifestUrl { get; set; } = string.Empty;

        public StatusItem SecondaryStatus { get; set; } = new StatusItem("Connection", "Not configured", ItemStatus.Unavailable);

        public ButtonItem TestSecondaryButton { get; set; } = new ButtonItem("Test Connection")
        {
            Icon = IconNames.network_check,
            Data1 = TestSecondaryCommand,
        };
    }
}
