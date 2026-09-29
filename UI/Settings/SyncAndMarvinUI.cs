using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements.List;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;

namespace InfiniteDrive.UI.Settings
{
    public class SyncAndMarvinUI : EditableOptionsBase
    {
        public const string RunMarvinNowCommand = nameof(RunMarvinNowCommand);
        public const string TogglePruningCommand = nameof(TogglePruningCommand);

        public override string EditorTitle => "Marvin";
        public override string EditorDescription =>
            "Marvin follows provider backoff, observed catalog state, and Emby's scheduled-task controls automatically.";

        // ═══════════════════════════════════════════════════════════════
        // Section 0: Marvin Status (top)
        // ═══════════════════════════════════════════════════════════════

        public StatusItem MarvinStatus { get; set; } = new StatusItem("Marvin", "Idle", ItemStatus.None);

        public CaptionItem ImportCaption { get; set; } = new CaptionItem("Import coverage");
        public StatusItem ImportStatus { get; set; } = new StatusItem("Coverage", "Not observed", ItemStatus.None);
        public ButtonItem ObserveImports { get; set; } = new ButtonItem("Observe imports") { Data1 = "ImportObserve" };
        public ButtonItem RepairImports { get; set; } = new ButtonItem("Enable recovery") { Data1 = "ImportRepair" };
        public ButtonItem StopImports { get; set; } = new ButtonItem("Off (legacy import continues)") { Data1 = "ImportOff" };
        public ButtonItem CheckImports { get; set; } = new ButtonItem("Check / retry due items") { Data1 = "ImportCheck" };
        public ButtonItem RefreshImports { get; set; } = new ButtonItem("Refresh coverage") { Data1 = "ImportRefresh" };
        public ButtonItem ResumeProvider { get; set; } = new ButtonItem("Retry provider after configuration fix") { Data1 = "ImportResumeProvider" };
        public GenericItemList ImportItems { get; set; } = new GenericItemList();
        public ButtonItem NextImports { get; set; } = new ButtonItem("Next coverage page") { Data1 = "ImportNext" };

        public SpacerItem SpacerStatus { get; set; } = new SpacerItem();

        // ═══════════════════════════════════════════════════════════════
        // Section 1: Marvin Process Schedule
        // ═══════════════════════════════════════════════════════════════

        public ButtonItem RunMarvinNowButton { get; set; } = new ButtonItem("Run Marvin Now")
        {
            Icon = IconNames.play_arrow,
            Data1 = RunMarvinNowCommand,
        };

    }
}
