using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;

namespace InfiniteDrive.UI.Settings
{
    public class SecurityUI : EditableOptionsBase
    {
        public const string RotateSecretCommand = nameof(RotateSecretCommand);

        public override string EditorTitle => "Security";
        public override string EditorDescription =>
            "The plugin secret is an HMAC-SHA256 key used to sign .strm file URLs. " +
            "It's auto-generated on first run and should not be shared. " +
            "Rotating it will invalidate all existing .strm files until the next catalog sync.";

        [DisplayName("Signature Validity (days)")]
        [Description("Days signed .strm URLs remain valid. Default: 365.")]
        public int SignatureValidityDays { get; set; } = 365;

        [DisplayName("Plugin Secret")]
        [Description("Auto-generated HMAC-SHA256 signing secret. Do not share this.")]
        [IsPassword]
        public string PluginSecret { get; set; } = string.Empty;

        public ButtonItem RotateSecretButton { get; set; } = new ButtonItem("Rotate Secret")
        {
            Icon = IconNames.vpn_key,
            Data1 = RotateSecretCommand,
            ConfirmationPrompt = "Rotating the secret invalidates all existing .strm files. Continue?"
        };
    }
}
