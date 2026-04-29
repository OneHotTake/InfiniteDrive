using System;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using InfiniteDrive.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.UI.Settings
{
    public class PlaybackTabView : PluginPageView
    {
        public PlaybackTabView(string pluginId, PlaybackUI ui) : base(pluginId)
        {
            ContentData = ui;
            LoadTiersAsync(ui).ConfigureAwait(false);
        }

        private PlaybackUI UI => (PlaybackUI)ContentData;

        public override bool IsCommandAllowed(string commandKey) => true;

        // ── Load ─────────────────────────────────────────────────────────────

        private async Task LoadTiersAsync(PlaybackUI ui)
        {
            try
            {
                var repo = Plugin.Instance?.VersionSlotRepository;
                if (repo == null) return;

                var slots = await repo.GetAllSlotsAsync().ConfigureAwait(false);
                ui.TierList.Clear();

                if (slots.Count == 0)
                {
                    ui.TierList.Add(new GenericListItem
                    {
                        PrimaryText = "No quality tiers defined — run a sync to generate defaults",
                        Icon = IconNames.info,
                        IconMode = ItemListIconMode.SmallRegular,
                    });
                    RaiseUIViewInfoChanged();
                    return;
                }

                var enabledCount = 0;
                foreach (var slot in slots)
                {
                    if (slot.Enabled) enabledCount++;

                    var details = slot.Resolution;
                    if (!string.IsNullOrEmpty(slot.HdrClasses) && slot.HdrClasses != "")
                        details += $" · {slot.HdrClasses.ToUpper()}";
                    if (!slot.AcceptsAnyCodec)
                        details += $" · {slot.VideoCodecs.ToUpper()}";

                    ui.TierList.Add(new GenericListItem
                    {
                        PrimaryText = $"{slot.Label}{(slot.IsDefault ? " (default)" : "")}",
                        SecondaryText = details,
                        Icon = slot.Enabled ? IconNames.check_circle : IconNames.radio_button_unchecked,
                        IconMode = ItemListIconMode.SmallRegular,
                        Status = slot.Enabled ? ItemStatus.Succeeded : ItemStatus.Unavailable,
                        Toggle = new ToggleButtonItem
                        {
                            IsChecked = slot.Enabled,
                            Caption = slot.IsDefault ? "Default" : "Enabled",
                            Data1 = slot.SlotKey,
                            CommandId = PlaybackUI.ToggleTierCommand,
                        },
                    });
                }

                ui.TierStatus.StatusText = $"{enabledCount} of {slots.Count} enabled";
                ui.TierStatus.Status = enabledCount > 0 ? ItemStatus.Succeeded : ItemStatus.Warning;

                RaiseUIViewInfoChanged();
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[PlaybackUI] Failed to load tiers");
            }
        }

        // ── Commands ─────────────────────────────────────────────────────────

        public override async Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            var effective = commandId;
            if (string.IsNullOrEmpty(effective) && !string.IsNullOrEmpty(data))
                effective = data.Split(':')[0];

            switch (effective)
            {
                case PlaybackUI.ToggleTierCommand:
                    // Data1 from ToggleButtonItem is passed in itemId or data
                    var slotKey = !string.IsNullOrEmpty(data) ? data : itemId;
                    if (!string.IsNullOrEmpty(slotKey))
                        await ToggleSlotAsync(slotKey);
                    return this;
            }

            return await base.RunCommand(itemId, commandId, data);
        }

        private async Task ToggleSlotAsync(string slotKey)
        {
            try
            {
                var repo = Plugin.Instance?.VersionSlotRepository;
                if (repo == null) return;

                var slot = await repo.GetSlotAsync(slotKey).ConfigureAwait(false);
                if (slot == null) return;

                // If disabling the default, pick the first enabled slot as new default
                if (slot.Enabled && slot.IsDefault)
                {
                    var allSlots = await repo.GetAllSlotsAsync().ConfigureAwait(false);
                    var nextDefault = allSlots.Find(s => s.Enabled && s.SlotKey != slotKey);
                    if (nextDefault == null)
                    {
                        UI.TierStatus.StatusText = "Cannot disable the only enabled tier";
                        UI.TierStatus.Status = ItemStatus.Warning;
                        RaiseUIViewInfoChanged();
                        return;
                    }
                    await repo.SetDefaultSlotAsync(nextDefault.SlotKey).ConfigureAwait(false);
                }

                slot.Enabled = !slot.Enabled;
                await repo.UpsertSlotAsync(slot).ConfigureAwait(false);

                Plugin.Instance?.Logger.LogInformation(
                    "[PlaybackUI] Toggled tier {SlotKey}: enabled={Enabled}", slotKey, slot.Enabled);

                // Reload the list
                await LoadTiersAsync(UI).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogWarning(ex, "[PlaybackUI] Failed to toggle tier");
            }
        }

        // ── Save ─────────────────────────────────────────────────────────────

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            var cfg = Plugin.Instance.Configuration;
            SettingsController.SavePlayback(UI, cfg);
            Plugin.Instance.SaveConfiguration();
            Plugin.Instance.TriggerBackgroundSync();
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
