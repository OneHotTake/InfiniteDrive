using System;

namespace Emby.Plugin.UI.Attributes
{
    /// <summary>
    /// Groups properties into a tab on the configuration page.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class TabGroupAttribute : Attribute
    {
        public string Name { get; set; } = string.Empty;
        public int Order { get; set; } = 0;

        public TabGroupAttribute()
        {
        }

        public TabGroupAttribute(string name)
        {
            Name = name;
        }
    }
}
