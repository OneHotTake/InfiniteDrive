using System;
using System.Collections.Generic;

namespace Emby.Plugin.UI.Attributes
{
    /// <summary>
    /// Defines filter options for a property.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class FilterOptionsAttribute : Attribute
    {
        public string[] Options { get; }

        public FilterOptionsAttribute(params string[] options)
        {
            Options = options;
        }
    }
}
