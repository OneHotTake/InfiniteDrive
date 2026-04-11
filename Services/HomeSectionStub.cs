using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive.Data;
using InfiniteDrive.Models;
using InfiniteDrive.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Stub ContentSection implementation for 4.9.x SDK compatibility.
    /// The 4.10.0.8-beta SDK has ContentSection.AddHomeSection API but it's not publicly available.
    /// This stub provides compatibility until 4.10.0.8-beta DLLs are released.
    /// Sprint 118: Home Screen Rails.
    /// </summary>
    public class StubContentSection
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string SectionType { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
    }

    /// <summary>
    /// Stub HomeSections implementation for 4.9.x SDK compatibility.
    /// Sprint 118: Home Screen Rails.
    /// </summary>
    public class StubHomeSections
    {
        public List<StubContentSection> Sections { get; set; } = new();
    }

    /// <summary>
    /// Extension methods to provide missing IUserManager APIs via stub implementations.
    /// Uses conditional compilation to work with both old and new SDK versions.
    /// Sprint 118: Home Screen Rails.
    /// </summary>
    public static class UserManagerExtensions
    {
        // In-memory storage for stub sections
        private static readonly Dictionary<long, List<StubContentSection>> _stubSections = new();

        /// <summary>
        /// Stub implementation of AddHomeSection for 4.9.x SDK compatibility.
        /// In 4.10.0.8-beta, this will be replaced with the real API.
        /// </summary>
#if !EMBY_HAS_CONTENTSECTION_API
        public static void AddHomeSection(this IUserManager userManager, long userId, object section, CancellationToken ct)
#else
        public static void AddHomeSection(this IUserManager userManager, long userId, object section, CancellationToken ct)
#endif
        {
#if !EMBY_HAS_CONTENTSECTION_API
            // Real API not available - use stub
            if (!_stubSections.ContainsKey(userId))
                _stubSections[userId] = new();

            var stubSection = section as StubContentSection;
            if (stubSection != null)
            {
                // Remove existing section with same ID
                _stubSections[userId] = _stubSections[userId]
                    .Where(s => s.Id != stubSection.Id)
                    .ToList();

                _stubSections[userId].Add(stubSection);
            }
#else
            // Real API available - call it
            // TODO: Implement actual API call when SDK is available
            // For now, this is a no-op stub that will be replaced
#endif
        }

        /// <summary>
        /// Stub implementation of GetHomeSections for 4.9.x SDK compatibility.
        /// In 4.10.0.8-beta, this will use the real API.
        /// </summary>
#if !EMBY_HAS_CONTENTSECTION_API
        public static StubHomeSections GetHomeSections(this IUserManager userManager, long userId, CancellationToken ct)
#else
        public static StubHomeSections GetHomeSections(this IUserManager userManager, long userId, CancellationToken ct)
#endif
        {
#if !EMBY_HAS_CONTENTSECTION_API
            // Real API not available - use stub
            if (!_stubSections.ContainsKey(userId))
                _stubSections[userId] = new();

            return new StubHomeSections { Sections = _stubSections.GetValueOrDefault(userId, new List<StubContentSection>()) };
#else
            // Real API available - call it
            // TODO: Implement actual API call when SDK is available
            // For now, return empty stub
            return new StubHomeSections { Sections = new List<StubContentSection>() };
#endif
        }
    }
}
