using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;

namespace InfiniteDrive.UI.Settings
{
    /// <summary>
    /// Playback settings — quality tiers and stream pre-selection.
    /// </summary>
    public class PlaybackUI : EditableOptionsBase
    {
        public const string ToggleTierCommand = nameof(ToggleTierCommand);
        public const string SetDefaultTierCommand = nameof(SetDefaultTierCommand);

        public override string EditorTitle => "Playback";
        public override string EditorDescription =>
            "Quality tiers and stream pre-selection. " +
            "Click a tier to toggle it on/off. The default tier plays automatically.";

        // ── Quality Tiers ──────────────────────────────────────────────────────

        public CaptionItem CaptionTiers { get; set; } = new CaptionItem("Quality Tiers");

        public LabelItem TiersDescription { get; set; } = new LabelItem(
            "Click a tier to enable/disable it. The default tier plays automatically; " +
            "others appear as alternate versions in Emby.");

        public GenericItemList TierList { get; set; } = new GenericItemList();

        public StatusItem TierStatus { get; set; } = new StatusItem("Tiers", "", ItemStatus.None);

        // ── Background Stream Selection ────────────────────────────────────────

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();
        public CaptionItem CaptionPreCache { get; set; } = new CaptionItem("Instant Playback");

        [DisplayName("Enable Background Stream Selection")]
        [Description(
            "When enabled, InfiniteDrive silently selects the best available stream for each title " +
            "before you press play. Browsing feels instant — no spinner waiting for AIOStreams. " +
            "Disable only if you want fully on-demand resolution.")]
        public bool EnablePreCache { get; set; } = true;
    }
}
