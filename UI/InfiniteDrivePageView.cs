using System;
using System.Threading.Tasks;
using Emby.Web.GenericEdit;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.Plugins.UI.Views;

namespace InfiniteDrive.UI
{
    public class InfiniteDrivePageView : IPluginUIView, IPluginPageView
    {
        private readonly EditableOptionsBase _content;
        private readonly Action<EditableOptionsBase> _onSave;
        private readonly Func<string, Task<string?>> _onCommand;
        private readonly Func<Task<IPluginUIView>>? _onRefresh;

        public InfiniteDrivePageView(
            EditableOptionsBase content,
            Action<EditableOptionsBase> onSave,
            Func<string, Task<string?>>? onCommand = null,
            Func<Task<IPluginUIView>>? onRefresh = null)
        {
            _content = content;
            _onSave = onSave;
            _onCommand = onCommand ?? (_ => Task.FromResult<string?>(null));
            _onRefresh = onRefresh;
        }

        // IPluginUIView
        public string Caption => _content.EditorTitle;
        public string SubCaption => _content.EditorDescription ?? string.Empty;
        public string PluginId => Plugin.PluginGuid.ToString();

        public IEditableObject ContentData
        {
            get => _content;
            set { /* deserialization handled by framework */ }
        }

        public UserDto User { get; set; } = null!;
        public string RedirectViewUrl { get; set; } = string.Empty;

        public event EventHandler<GenericEventArgs<IPluginUIView>> UIViewInfoChanged
        {
            add { }
            remove { }
        }

        public bool IsCommandAllowed(string commandKey) => true;

        public async Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            // Server-side view refresh: return a completely new view with fresh data
            if (commandId == "refresh" && _onRefresh != null)
            {
                try
                {
                    return await _onRefresh().ConfigureAwait(false);
                }
                catch
                {
                    return this; // return current view on refresh failure
                }
            }

            try
            {
                await _onCommand(commandId).ConfigureAwait(false);
            }
            catch
            {
                // swallow — command handlers return status strings, not exceptions
            }
            return this;
        }

        public Task Cancel() => Task.CompletedTask;

        public void OnDialogResult(IPluginUIView dialogView, bool completedOk, object data) { }

        // IPluginPageView
        public bool ShowSave { get; set; } = true;
        public bool ShowBack { get; set; } = false;
        public bool AllowSave { get; set; } = true;
        public bool AllowBack { get; set; } = true;

        public Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            _onSave(_content);
            return Task.FromResult<IPluginUIView>(this);
        }
    }
}
