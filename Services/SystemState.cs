using InfiniteDrive.Models;

namespace InfiniteDrive.Services
{
    public class ProviderHealth
    {
        public string ProviderId { get; set; } = string.Empty;
        public bool IsConfigured { get; set; }
        public bool IsReachable { get; set; }
        public string LastTestAt { get; set; } = string.Empty;
        public int LatencyMs { get; set; } = -1;
        public string Message { get; set; } = string.Empty;
        public string ExpiresAt { get; set; } = string.Empty;
    }

    public class LibraryHealth
    {
        public bool IsConfigured { get; set; }
        public bool IsAccessible { get; set; }
        public int CatalogItemCount { get; set; }
        public int StrmFileCount { get; set; }
    }

    public class SystemSnapshot
    {
        public SystemStateEnum State { get; set; } = SystemStateEnum.Unconfigured;
        public string Description { get; set; } = string.Empty;
        public string EvaluatedAt { get; set; } = string.Empty;
        public ProviderHealth PrimaryProvider { get; set; } = new();
        public ProviderHealth SecondaryProvider { get; set; } = new();
        public LibraryHealth Library { get; set; } = new();

        public bool AnyProviderReachable =>
            PrimaryProvider.IsReachable || SecondaryProvider.IsReachable;

        public bool AllProvidersReachable =>
            (!PrimaryProvider.IsConfigured || PrimaryProvider.IsReachable) &&
            (!SecondaryProvider.IsConfigured || SecondaryProvider.IsReachable);
    }
}
