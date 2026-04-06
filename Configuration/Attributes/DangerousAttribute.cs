using System;

namespace Emby.Plugin.UI.Attributes
{
    /// <summary>
    /// Marks a button as dangerous (requires confirmation).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class DangerousAttribute : Attribute
    {
        public string Message { get; }

        public DangerousAttribute(string message = "This action cannot be undone")
        {
            Message = message;
        }
    }
}
