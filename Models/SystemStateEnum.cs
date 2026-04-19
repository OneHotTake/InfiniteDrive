using System.Text.Json.Serialization;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// Overall system health state evaluated by SystemStateService.
    /// Marvin uses this as a gate — only executes if Ready or Degraded.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<SystemStateEnum>))]
    public enum SystemStateEnum
    {
        Unconfigured,
        Provisioning,
        Ready,
        Degraded,
        Error
    }
}
