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

        #region Actions Tab

        // Actions are handled via Controller endpoints, not ViewModel properties
        // The UI buttons will call these endpoints directly

        #endregion
    }
}
