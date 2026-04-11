using System;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Parses year values from various release info formats.
    /// Sprint 100B-07: GetYear with ReleaseInfo range.
    /// Handles: "2015", "2007-2019", "2020-", null/empty.
    /// </summary>
    public static class YearParser
    {
        /// <summary>
        /// Parses the first year from a release info string.
        /// Supports:
        /// - Single year: "2015" → 2015
        /// - Year range: "2007-2019" → 2007
        /// - Ongoing: "2020-" → 2020
        /// - Null/empty: null/"" → null
        /// </summary>
        /// <param name="releaseInfo">Release info string from metadata.</param>
        /// <returns>Parsed year or null if invalid.</returns>
        public static int? Parse(string? releaseInfo)
        {
            if (string.IsNullOrWhiteSpace(releaseInfo))
                return null;

            // Handle en-dash and em-dash variants
            var parts = releaseInfo!.Split(new[] { '–', '-', '—' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
                return null;

            // Try to parse the first part as a year
            if (int.TryParse(parts[0].Trim(), out var year))
            {
                // Sanity check: year should be reasonable (between 1888 and current year + 5)
                var currentYear = DateTime.UtcNow.Year;
                if (year >= 1888 && year <= currentYear + 5)
                    return year;
            }

            return null;
        }

        /// <summary>
        /// Parses a year range into start and end years.
        /// Supports:
        /// - Single year: "2015" → (2015, null)
        /// - Year range: "2007-2019" → (2007, 2019)
        /// - Ongoing: "2020-" → (2020, null)
        /// - Null/empty: null/"" → (null, null)
        /// </summary>
        /// <param name="releaseInfo">Release info string from metadata.</param>
        /// <returns>Tuple of (startYear, endYear) or (null, null) if invalid.</returns>
        public static (int? start, int? end) ParseRange(string? releaseInfo)
        {
            if (string.IsNullOrWhiteSpace(releaseInfo))
                return (null, null);

            // Handle en-dash and em-dash variants
            var parts = releaseInfo!.Split(new[] { '–', '-', '—' }, StringSplitOptions.RemoveEmptyEntries);

            int? startYear = null;
            int? endYear = null;

            var currentYear = DateTime.UtcNow.Year;

            // Parse start year
            if (parts.Length > 0 && int.TryParse(parts[0].Trim(), out var start))
            {
                if (start >= 1888 && start <= currentYear + 5)
                    startYear = start;
            }

            // Parse end year (if present)
            if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out var end))
            {
                if (end >= 1888 && end <= currentYear + 5)
                    endYear = end;
            }

            return (startYear, endYear);
        }
    }
}
