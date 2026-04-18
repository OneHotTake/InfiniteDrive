using System;
using System.Collections.Generic;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Dynamic <see cref="IExternalId"/> — auto-discovered by Emby.
    /// Reads the census from configuration to determine which ID type to represent.
    /// Since Emby auto-creates this, it defaults to the first non-native type
    /// in the census, or "InfiniteDrive" if no census exists.
    /// </summary>
    public class AioDynamicExternalId : IExternalId
    {
        private static readonly Dictionary<string, (string Name, string? Url)> KnownNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Kitsu"]   = ("Kitsu",        "https://kitsu.io/anime/{0}"),
            ["MAL"]     = ("MyAnimeList",  "https://myanimelist.net/anime/{0}"),
            ["AniDB"]   = ("AniDB",        "https://anidb.net/a{0}"),
            ["AniList"] = ("AniList",      "https://anilist.co/anime/{0}"),
            ["IMDB"]    = ("IMDb",         "https://www.imdb.com/title/{0}"),
            ["TMDB"]    = ("TheMovieDB",   "https://www.themoviedb.org/movie/{0}"),
            ["TVDB"]    = ("TheTVDB",      "https://thetvdb.com/?tab=series&id={0}"),
        };

        public string Key => "InfiniteDrive";
        public string Name => "InfiniteDrive";
        public string? UrlFormatString => null;
        public bool Supports(IHasProviderIds item) => item is Series || item is Movie;

        /// <summary>
        /// Resolves the display name for a provider key (used by UI).
        /// </summary>
        public static string GetDisplayName(string key)
        {
            if (KnownNames.TryGetValue(key, out var info))
                return info.Name;
            return string.IsNullOrEmpty(key) ? key
                : char.ToUpper(key[0]) + key[1..].ToLowerInvariant();
        }

        /// <summary>
        /// Resolves the URL template for a provider key, if known.
        /// </summary>
        public static string? GetUrlFormat(string key)
        {
            return KnownNames.TryGetValue(key, out var info) ? info.Url : null;
        }
    }
}
