using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;

namespace InfiniteDrive.UI.Settings
{
    public class ContentControlsUI : EditableOptionsBase
    {
        public const string AddToBlockListCommand = nameof(AddToBlockListCommand);
        public const string UnblockItemCommand = nameof(UnblockItemCommand);

        public override string EditorTitle => "Content Controls";
        public override string EditorDescription =>
            "Quality preferences, parental controls, and blocked content management.";

        // ═══════════════════════════════════════════════════════════════
        // Section 1: Quality & Resolution Preferences
        // ═══════════════════════════════════════════════════════════════

        public CaptionItem CaptionQuality { get; set; } = new CaptionItem("Quality & Resolution Preferences");

        [DisplayName("Use REMUX files for auto-selection")]
        [Description(
            "When enabled, 4K/1080p Bluray REMUX files (40-60GB, TrueHD/Atmos audio) are included in " +
            "auto-selection. REMUX files take 40+ seconds to probe and often require transcoding. " +
            "When disabled (recommended), REMUX is deprioritized in favor of faster-starting encodes.")]
        public bool UseRemuxForAutoSelection { get; set; } = false;

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();

        public LabelItem QualityTierHelp { get; set; } = new LabelItem(
            "Max streams per quality tier shown in the version dropdown. " +
            "0 = tier disabled. Default: 2 per tier.");

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> ZeroToFiveOptions { get; set; } = new List<EditorSelectOption>
        {
            new() { Value = "0", Name = "0 (disabled)", IsEnabled = true },
            new() { Value = "1", Name = "1", IsEnabled = true },
            new() { Value = "2", Name = "2", IsEnabled = true },
            new() { Value = "3", Name = "3", IsEnabled = true },
            new() { Value = "4", Name = "4", IsEnabled = true },
            new() { Value = "5", Name = "5", IsEnabled = true },
        };

        [DisplayName("4K 5.1 / DTS")]
        [Description("4K encodes with 5.1 surround sound or DTS audio.")]
        [SelectItemsSource(nameof(ZeroToFiveOptions))]
        public string MaxStreams4k51 { get; set; } = "2";

        [DisplayName("4K (any)")]
        [Description("Any 4K encode regardless of audio format.")]
        [SelectItemsSource(nameof(ZeroToFiveOptions))]
        public string MaxStreams4kAny { get; set; } = "2";

        [DisplayName("1080p 5.1")]
        [Description("1080p encodes with 5.1 surround sound.")]
        [SelectItemsSource(nameof(ZeroToFiveOptions))]
        public string MaxStreams1080p51 { get; set; } = "2";

        [DisplayName("1080p (any)")]
        [Description("Any 1080p encode regardless of audio format.")]
        [SelectItemsSource(nameof(ZeroToFiveOptions))]
        public string MaxStreams1080pAny { get; set; } = "2";

        [DisplayName("720p")]
        [Description("720p encodes. Good balance of quality and bandwidth.")]
        [SelectItemsSource(nameof(ZeroToFiveOptions))]
        public string MaxStreams720p { get; set; } = "2";

        [DisplayName("SD / Unknown / Low-bandwidth")]
        [Description("Standard definition, unknown quality, or low-bandwidth streams.")]
        [SelectItemsSource(nameof(ZeroToFiveOptions))]
        public string MaxStreamsSd { get; set; } = "2";

        public SpacerItem Spacer1_5 { get; set; } = new SpacerItem();

        // Options for the Default Quality Tier dropdown
        [Browsable(false)]
        public IEnumerable<EditorSelectOption> DefaultQualityTierOptions { get; set; } = new List<EditorSelectOption>
        {
            new() { Value = "4K 5.1 / DTS",          Name = "4K 5.1 / DTS", IsEnabled = true },
            new() { Value = "4K (any)",              Name = "4K (any)", IsEnabled = true },
            new() { Value = "1080p 5.1",             Name = "1080p 5.1", IsEnabled = true },
            new() { Value = "1080p (any)",           Name = "1080p (any)", IsEnabled = true },
            new() { Value = "720p",                  Name = "720p", IsEnabled = true },
            new() { Value = "SD / Unknown / Low-bandwidth", Name = "SD / Unknown / Low-bandwidth", IsEnabled = true },
        };

        [DisplayName("Default Quality Tier")]
        [Description("Quality tier that plays automatically when multiple streams are available.")]
        [SelectItemsSource(nameof(DefaultQualityTierOptions))]
        public string DefaultQualityTier { get; set; } = "1080p (any)";

        // ═══════════════════════════════════════════════════════════════
        // Section 2: Parental Controls (Discover-only)
        // ═══════════════════════════════════════════════════════════════

        public SpacerItem Spacer2 { get; set; } = new SpacerItem();
        public CaptionItem CaptionParental { get; set; } = new CaptionItem("Parental Controls (Discover-only)");

        [DisplayName("Hide unrated content")]
        [Description(
            "Only affects InfiniteDrive Discover / search / browse results. " +
            "Emby native library restrictions still apply for persisted items.")]
        public bool HideUnratedContent { get; set; } = false;

        // ═══════════════════════════════════════════════════════════════
        // Section 3: Blocked Content Management
        // ═══════════════════════════════════════════════════════════════

        public SpacerItem Spacer3 { get; set; } = new SpacerItem();
        public CaptionItem CaptionBlocked { get; set; } = new CaptionItem("Blocked Content Management");

        [DisplayName("Block by Title or ID")]
        [Description("Enter a movie/show title, IMDB ID (tt1234567), TMDB ID, Kitsu ID, or AniList ID to block. Then click 'Add to Block List'.")]
        public string BlockListInput { get; set; } = string.Empty;

        public ButtonItem AddToBlockListButton { get; set; } = new ButtonItem("Add to Block List")
        {
            Icon = IconNames.add,
            Data1 = AddToBlockListCommand,
        };

        public StatusItem BlockListStatus { get; set; } = new StatusItem("Block List", "Idle", ItemStatus.None);

        public GenericItemList BlockedItemList { get; set; } = new GenericItemList();

        // ═══════════════════════════════════════════════════════════════
        // Helpers
        // ═══════════════════════════════════════════════════════════════

        private static readonly Dictionary<string, string> TierToProperty = new()
        {
            { "4K 5.1 / DTS",                  nameof(MaxStreams4k51) },
            { "4K (any)",                       nameof(MaxStreams4kAny) },
            { "1080p 5.1",                      nameof(MaxStreams1080p51) },
            { "1080p (any)",                    nameof(MaxStreams1080pAny) },
            { "720p",                           nameof(MaxStreams720p) },
            { "SD / Unknown / Low-bandwidth",   nameof(MaxStreamsSd) },
        };

        public Dictionary<string, int> GetTierLimits()
        {
            var limits = new Dictionary<string, int>();
            foreach (var kvp in TierToProperty)
            {
                var prop = GetType().GetProperty(kvp.Value);
                if (prop != null && int.TryParse((string?)prop.GetValue(this), out var val))
                    limits[kvp.Key] = val;
                else
                    limits[kvp.Key] = 2;
            }
            return limits;
        }

        public void SetTierLimits(Dictionary<string, int> limits)
        {
            foreach (var kvp in TierToProperty)
            {
                if (limits.TryGetValue(kvp.Key, out var val))
                {
                    var prop = GetType().GetProperty(kvp.Value);
                    if (prop != null)
                        prop.SetValue(this, val.ToString());
                }
            }
        }
    }
}
