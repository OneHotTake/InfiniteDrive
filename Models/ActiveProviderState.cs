namespace InfiniteDrive.Models
{
    public enum ActiveProvider
    {
        Primary,
        Secondary
    }

    /// <summary>
    /// Tracks which provider is currently active for stream resolution.
    /// Sprint 311: Self-healing failover state.
    /// </summary>
    public class ActiveProviderState
    {
        private readonly object _lock = new();
        private volatile ActiveProvider _current = ActiveProvider.Primary;

        public ActiveProvider Current
        {
            get { lock (_lock) return _current; }
            set { lock (_lock) _current = value; }
        }
    }
}
