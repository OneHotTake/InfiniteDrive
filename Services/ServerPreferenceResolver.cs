using System;
using System.Linq;
using MediaBrowser.Controller.Library;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Reads locale preferences from Emby's library state. InfiniteDrive does not
    /// maintain a competing language/country configuration surface.
    /// </summary>
    public static class ServerPreferenceResolver
    {
        private static ILibraryManager? _libraryManager;

        public static void Initialize(ILibraryManager libraryManager) =>
            _libraryManager = libraryManager;

        public static string MetadataLanguage(string? itemPath = null) =>
            Find(itemPath, options => options.PreferredMetadataLanguage)
            ?? RuntimePolicy.FallbackLanguage;

        public static string ImageLanguage(string? itemPath = null) =>
            Find(itemPath, options => options.PreferredImageLanguage)
            ?? MetadataLanguage(itemPath);

        public static string CertificationCountry(string? itemPath = null) =>
            Find(itemPath, options => options.MetadataCountryCode)
            ?? RuntimePolicy.FallbackCountry;

        private static string? Find(
            string? itemPath,
            Func<MediaBrowser.Model.Configuration.LibraryOptions, string?> selector)
        {
            try
            {
                var folders = _libraryManager?.GetVirtualFolders();
                if (folders == null) return null;

                if (!string.IsNullOrWhiteSpace(itemPath))
                {
                    foreach (var folder in folders)
                    {
                        if (folder.Locations?.Any(location =>
                                !string.IsNullOrWhiteSpace(location)
                                && itemPath.StartsWith(location, StringComparison.OrdinalIgnoreCase)) != true)
                            continue;

                        var value = folder.LibraryOptions == null ? null : selector(folder.LibraryOptions);
                        if (!string.IsNullOrWhiteSpace(value)) return value;
                    }
                }

                foreach (var folder in folders)
                {
                    var value = folder.LibraryOptions == null ? null : selector(folder.LibraryOptions);
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
            catch
            {
                // Locale lookup is advisory; deterministic fallbacks are above.
            }

            return null;
        }
    }
}
