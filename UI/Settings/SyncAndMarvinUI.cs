using Emby.Web.GenericEdit;
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
