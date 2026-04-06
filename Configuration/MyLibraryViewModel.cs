using System.Collections.Generic;
using Emby.Plugin.UI.Attributes;

namespace EmbyStreams.Configuration
{
    /// <summary>
    /// My Library ViewModel for per-user personalization.
    /// Shows tabs for Saved, Blocked, and Watch History.
    /// </summary>
    public class MyLibraryViewModel : BasePluginViewModel
    {
        #region Saved Tab

        [TabGroup("Saved", Order = 1)]
        [DataGrid]
        public List<ItemRow> SavedItems { get; set; } = new();

        #endregion

        #region Blocked Tab

        [TabGroup("Blocked", Order = 1)]
        [DataGrid]
        public List<ItemRow> BlockedItems { get; set; } = new();

        #endregion

        #region Watch History Tab

        [TabGroup("Watch History", Order = 1)]
        [DataGrid]
        public List<WatchHistoryRow> WatchHistory { get; set; } = new();

        #endregion
    }
}
