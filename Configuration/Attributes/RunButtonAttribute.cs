using System;

namespace Emby.Plugin.UI.Attributes
{
    /// <summary>
    /// Marks a method to be displayed as a button in the UI.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class RunButtonAttribute : Attribute
    {
        public string Label { get; }
        public string? Confirmation { get; set; }

        public RunButtonAttribute(string? label = null)
        {
            Label = label ?? "Run";
        }
    }
}
