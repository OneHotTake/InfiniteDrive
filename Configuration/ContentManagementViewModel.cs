using System.Collections.Generic;
using Emby.Plugin.UI.Attributes;

namespace EmbyStreams.Configuration
{
    /// <summary>
    /// Content Management ViewModel for admin-only functions.
    /// Shows tabs for Sources, Collections, Items, and Actions.
    /// </summary>
    public class ContentManagementViewModel : BasePluginViewModel
    {
        #region Sources Tab

        [TabGroup("Sources", Order = 1)]
        [DataGrid]
        public List<SourceRow> Sources { get; set; } = new();

        #endregion

        #region Collections Tab

        [TabGroup("Collections", Order = 1)]
        [DataGrid]
        public List<CollectionRow> Collections { get; set; } = new();

        #endregion

        #region Items Tab

        [TabGroup("Items", Order = 1)]
        [DataGrid]
        public List<ItemRow> AllItems { get; set; } = new();

        #endregion

        #region Needs Review Tab

        [TabGroup("Needs Review", Order = 1)]
        [DataGrid]
        public List<ItemRow> NeedsReview { get; set; } = new();

        #endregion

        #region Stream Versions Tab

        [TabGroup("Stream Versions", Order = 1)]
        public bool SlotHdBroad { get; set; } = true;

        [TabGroup("Stream Versions", Order = 2)]
        public bool SlotBestAvailable { get; set; } = false;

        [TabGroup("Stream Versions", Order = 3)]
        public bool Slot4kDv { get; set; } = false;

        [TabGroup("Stream Versions", Order = 4)]
        public bool Slot4kHdr { get; set; } = false;

        [TabGroup("Stream Versions", Order = 5)]
        public bool Slot4kSdr { get; set; } = false;

        [TabGroup("Stream Versions", Order = 6)]
        public bool SlotHdEfficient { get; set; } = false;

        [TabGroup("Stream Versions", Order = 7)]
        public bool SlotCompact { get; set; } = false;

        [TabGroup("Stream Versions", Order = 8)]
        public string DefaultVersion { get; set; } = "hd_broad";

        [TabGroup("Stream Versions", Order = 9)]
        public string VersionCount { get; set; } = "Enabled: 1 / 8";

        #endregion

        #region Actions Tab

        // Actions are handled via Controller endpoints, not ViewModel properties
        // The UI buttons will call these endpoints directly

        #endregion
    }
}
