using System;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;

namespace InfiniteDrive.UI
{
    public class SecurityUI : EditableOptionsBase
    {
        public override string EditorTitle => "Security";

        [DisplayName("Plugin Secret")]
        [Description("HMAC-SHA256 signing secret for .strm file URLs. Auto-generated on first load.")]
        [IsPassword]
        public string PluginSecret { get; set; } = string.Empty;

        [DisplayName("Signature Validity (days)")]
        [Description("Number of days signed .strm URLs remain valid. Default: 365.")]
        public int SignatureValidityDays { get; set; } = 365;

        [DisplayName("Last Rotated")]
        [Description("Timestamp of the last secret rotation.")]
        public string PluginSecretRotatedAt { get; set; } = "Never";

        [DisplayName("Rotate Secret")]
        public ButtonItem RotateButton => new ButtonItem
        {
            Caption = "Rotate Secret",
            CommandId = "rotate",
            ConfirmationPrompt = "Rotating the secret invalidates ALL existing .strm files. A full catalog sync will be needed. Continue?"
        };

        public SecurityUI() { }

        public SecurityUI(PluginConfiguration cfg)
        {
            PluginSecret = cfg.PluginSecret;
            SignatureValidityDays = cfg.SignatureValidityDays;
            PluginSecretRotatedAt = cfg.PluginSecretRotatedAt > 0
                ? DateTimeOffset.FromUnixTimeSeconds(cfg.PluginSecretRotatedAt).ToLocalTime().ToString("g")
                : "Never";
        }

        public void ApplyTo(PluginConfiguration cfg)
        {
            cfg.SignatureValidityDays = SignatureValidityDays;
            // PluginSecret is read-only in UI — rotation is done via button command
        }
    }
}
