using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Scans library for user-added files ("Your Files").
    /// Filters out EmbyStreams-managed items (.strm files).
    /// </summary>
    public class YourFilesScanner
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<YourFilesScanner> _logger;

        public YourFilesScanner(ILibraryManager libraryManager, ILogger<YourFilesScanner> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        /// <summary>
        /// Scans library for user-added files.
        /// </summary>
        public Task<List<BaseItem>> ScanAsync(CancellationToken ct = default)
        {
            _logger.LogInformation("[YourFilesScanner] Scanning library for 'Your Files'...");

            // Query all items that are NOT EmbyStreams-managed
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie", "Episode" },
                IsFolder = false
            };

            var allItems = _libraryManager.GetItemList(query)
                .ToList();

            // Filter: exclude items we created (have .strm files)
            var yourFilesItems = allItems
                .Where(item => !IsInfiniteDriveItem(item))
                .ToList();

            _logger.LogInformation("[YourFilesScanner] Found {Count} 'Your Files' items", yourFilesItems.Count);

            return Task.FromResult(yourFilesItems);
        }

        /// <summary>
        /// Checks if an item is managed by EmbyStreams.
        /// </summary>
        private bool IsInfiniteDriveItem(BaseItem item)
        {
            // Check if item has .strm extension
            if (item.Path != null && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Check if item has InfiniteDrive provider ID
            if (item.ProviderIds != null && item.ProviderIds.ContainsKey("embystreams"))
            {
                return true;
            }

            return false;
        }
    }
}
