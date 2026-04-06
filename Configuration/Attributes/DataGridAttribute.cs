using System;

namespace Emby.Plugin.UI.Attributes
{
    /// <summary>
    /// Marks a collection property to be displayed as a data grid.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class DataGridAttribute : Attribute
    {
        public int PageSize { get; set; } = 50;
        public bool AllowSort { get; set; } = true;
        public bool AllowFilter { get; set; } = true;
    }
}
