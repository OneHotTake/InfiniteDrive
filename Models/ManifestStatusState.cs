using System.Runtime.Serialization;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// Manifest fetch status. Backward-compatible string values via EnumMember.
    /// </summary>
    public enum ManifestStatusState
    {
        [EnumMember(Value = "error")]
        Error = 0,

        [EnumMember(Value = "notConfigured")]
        NotConfigured = 1,

        [EnumMember(Value = "stale")]
        Stale = 2,

        [EnumMember(Value = "ok")]
        Ok = 3
    }
}
