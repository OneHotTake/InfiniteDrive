namespace InfiniteDrive.Configuration.UI
{
    using System;
    using System.Threading.Tasks;

    using MediaBrowser.Model.Attributes;
    using MediaBrowser.Model.Dto;
    using MediaBrowser.Model.Events;
    using MediaBrowser.Model.GenericEdit;
    using MediaBrowser.Model.Plugins.UI.Views;
    using MediaBrowser.Model.Plugins.UI.Views.Enums;

    /// <summary>
    /// Base class for plugin UI views implementing IPluginUIView and IPluginViewWithOptions.
    /// Adapted from Emby SDK demo UIBaseClasses/Views/PluginViewBase.cs.
    /// </summary>
    public abstract class PluginViewBase : IPluginUIView, IPluginViewWithOptions
    {
        protected PluginViewBase(string pluginId)
        {
            PluginId = pluginId;
        }

        public event EventHandler<GenericEventArgs<IPluginUIView>> UIViewInfoChanged;

        public virtual string Caption => ContentData?.EditorTitle ?? string.Empty;

        public virtual string SubCaption => ContentData?.EditorDescription ?? string.Empty;

        public string PluginId { get; }

        public IEditableObject ContentData
        {
            get => ContentDataCore;
            set => ContentDataCore = value;
        }

        public UserDto User { get; set; }

        public string RedirectViewUrl { get; set; }

        public Uri HelpUrl { get; set; }

        public QueryCloseAction QueryCloseAction { get; set; }

        public WizardHidingBehavior WizardHidingBehavior { get; set; }

        public CompactViewAppearance CompactViewAppearance { get; set; }

        public DialogSize DialogSize { get; set; }

        public string OKButtonCaption { get; set; }

        public DialogAction PrimaryDialogAction { get; set; }

        protected IEditableObject ContentDataCore { get; set; }

        public virtual bool IsCommandAllowed(string commandKey) => true;

        public virtual Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            return Task.FromResult<IPluginUIView>(null!);
        }

        public virtual Task Cancel() => Task.CompletedTask;

        public virtual void OnDialogResult(IPluginUIView dialogView, bool completedOk, object data) { }

        protected virtual void RaiseUIViewInfoChanged()
        {
            UIViewInfoChanged?.Invoke(this, new GenericEventArgs<IPluginUIView>(this));
        }

        public virtual PluginViewOptions ViewOptions => new PluginViewOptions
        {
            HelpUrl = HelpUrl,
            CompactViewAppearance = CompactViewAppearance,
            QueryCloseAction = QueryCloseAction,
            DialogSize = DialogSize,
            OKButtonCaption = OKButtonCaption,
            PrimaryDialogAction = PrimaryDialogAction,
            WizardHidingBehavior = WizardHidingBehavior,
        };
    }
}
