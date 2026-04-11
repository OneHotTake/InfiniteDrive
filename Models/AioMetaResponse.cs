using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// Strongly-typed deserialization model for Stremio/AIO metadata responses.
    /// Sprint 101A-02: AIOMetadata deserialization.
    ///
    /// Handles the standard Stremio meta response format:
    /// {
    ///   "meta": {
    ///     "id": "string",
    ///     "type": "movie|series",
    ///     "name": "string",
    ///     "poster": "string",
    ///     "background": "string",
    ///     "logo": "string",
    ///     "description": "string",
    ///     "releaseInfo": "string",
    ///     "imdbRating": "string",
    ///     "imdbId": "string",
    ///     "tmdbId": "string",
    ///     "genres": ["string"],
    ///     "cast": ["string"],
    ///     "director": "string",
    ///     "runtime": "string",
    ///     "country": "string",
    ///     "year": "number|string",
    ///     "videos": [...],
    ///     "behaviorHints": {...}
    ///   }
    /// }
    /// </summary>
    public class AioMetaResponse
    {
        /// <summary>
        /// The wrapped metadata object (standard Stremio format).
        /// </summary>
        [JsonPropertyName("meta")]
        public AioMeta? Meta { get; set; }

        /// <summary>
        /// Gets the inner metadata, handling both wrapped and direct formats.
        /// Some providers return the metadata directly, others wrap it in a "meta" property.
        /// </summary>
        public AioMeta? GetMetadata()
        {
            // If meta is present, use it
            if (Meta != null) return Meta;

            // If no meta wrapper, this might be a direct response
            // Return null - caller should handle JsonElement responses
            return null;
        }
    }

    /// <summary>
    /// Core metadata properties from Stremio/AIO meta responses.
    /// </summary>
    public class AioMeta
    {
        /// <summary>
        /// The item's unique identifier (typically IMDB ID with or without "tt" prefix).
        /// </summary>
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        /// <summary>
        /// The item type: "movie" or "series".
        /// </summary>
        [JsonPropertyName("type")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public AioMetaType Type { get; set; }

        /// <summary>
        /// The primary title of the item.
        /// </summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        /// <summary>
        /// Poster image URL.
        /// </summary>
        [JsonPropertyName("poster")]
        public string? Poster { get; set; }

        /// <summary>
        /// Background/fanart image URL.
        /// </summary>
        [JsonPropertyName("background")]
        public string? Background { get; set; }

        /// <summary>
        /// Logo image URL.
        /// </summary>
        [JsonPropertyName("logo")]
        public string? Logo { get; set; }

        /// <summary>
        /// Plot/summary description.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>
        /// Release information (varies by provider).
        /// </summary>
        [JsonPropertyName("releaseInfo")]
        public string? ReleaseInfo { get; set; }

        /// <summary>
        /// IMDB rating as a string (e.g., "7.5").
        /// </summary>
        [JsonPropertyName("imdbRating")]
        public string? ImdbRating { get; set; }

        /// <summary>
        /// IMDB ID (may be with or without "tt" prefix).
        /// </summary>
        [JsonPropertyName("imdbId")]
        public string? ImdbId { get; set; }

        /// <summary>
        /// TMDB ID.
        /// </summary>
        [JsonPropertyName("tmdbId")]
        public string? TmdbId { get; set; }

        /// <summary>
        /// AniList ID (anime-specific).
        /// </summary>
        [JsonPropertyName("anilist_id")]
        public string? AnilistId { get; set; }

        /// <summary>
        /// Kitsu ID (anime-specific).
        /// </summary>
        [JsonPropertyName("kitsu_id")]
        public string? KitsuId { get; set; }

        /// <summary>
        /// MyAnimeList ID (anime-specific).
        /// </summary>
        [JsonPropertyName("mal_id")]
        public string? MalId { get; set; }

        /// <summary>
        /// AniDB ID (anime-specific).
        /// </summary>
        [JsonPropertyName("anidb")]
        public string? Anidb { get; set; }

        /// <summary>
        /// List of genre names.
        /// </summary>
        [JsonPropertyName("genres")]
        public List<string>? Genres { get; set; }

        /// <summary>
        /// List of cast member names.
        /// </summary>
        [JsonPropertyName("cast")]
        public List<string>? Cast { get; set; }

        /// <summary>
        /// Director name(s).
        /// </summary>
        [JsonPropertyName("director")]
        public string? Director { get; set; }

        /// <summary>
        /// Runtime duration (varies by provider).
        /// </summary>
        [JsonPropertyName("runtime")]
        public string? Runtime { get; set; }

        /// <summary>
        /// Country of origin.
        /// </summary>
        [JsonPropertyName("country")]
        public string? Country { get; set; }

        /// <summary>
        /// Release year (can be number or string).
        /// </summary>
        [JsonPropertyName("year")]
        [JsonConverter(typeof(YearConverter))]
        public int? Year { get; set; }

        /// <summary>
        /// Collection name (if the item belongs to a collection/franchise).
        /// </summary>
        [JsonPropertyName("collection")]
        public string? Collection { get; set; }

        /// <summary>
        /// Belongs to collection (alternative field name).
        /// </summary>
        [JsonPropertyName("belongsToCollection")]
        public string? BelongsToCollection { get; set; }

        /// <summary>
        /// Videos/episodes list (for series).
        /// </summary>
        [JsonPropertyName("videos")]
        public List<AioVideo>? Videos { get; set; }

        /// <summary>
        /// Behavior hints for the player.
        /// </summary>
        [JsonPropertyName("behaviorHints")]
        public AioBehaviorHints? BehaviorHints { get; set; }

        /// <summary>
        /// Original title (non-English).
        /// </summary>
        [JsonPropertyName("originalTitle")]
        public string? OriginalTitle { get; set; }

        /// <summary>
        /// Sort title (alternative title for sorting).
        /// </summary>
        [JsonPropertyName("sortTitle")]
        public string? SortTitle { get; set; }
    }

    /// <summary>
    /// Metadata type enumeration.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AioMetaType
    {
        /// <summary>Movie content type.</summary>
        Movie = 0,

        /// <summary>Series content type.</summary>
        Series = 1,

        /// <summary>Other/unknown content type.</summary>
        Other = 99
    }

    /// <summary>
    /// Video/episode information for series items.
    /// </summary>
    public class AioVideo
    {
        /// <summary>
        /// Video ID (typically provider:seriesId:seasonEpisode format).
        /// </summary>
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        /// <summary>
        /// Episode title.
        /// </summary>
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        /// <summary>
        /// Season number.
        /// </summary>
        [JsonPropertyName("season")]
        public int? Season { get; set; }

        /// <summary>
        /// Episode number.
        /// </summary>
        [JsonPropertyName("episode")]
        public int? Episode { get; set; }

        /// <summary>
        /// Episode release info.
        /// </summary>
        [JsonPropertyName("released")]
        public string? Released { get; set; }

        /// <summary>
        /// Thumbnail/preview image URL.
        /// </summary>
        [JsonPropertyName("thumbnail")]
        public string? Thumbnail { get; set; }

        /// <summary>
        /// Behavior hints for this specific video.
        /// </summary>
        [JsonPropertyName("behaviorHints")]
        public AioBehaviorHints? BehaviorHints { get; set; }
    }

    /// <summary>
    /// Player behavior hints.
    /// </summary>
    public class AioBehaviorHints
    {
        /// <summary>
        /// Indicates if this is a live stream.
        /// </summary>
        [JsonPropertyName("isLive")]
        public bool? IsLive { get; set; }

        /// <summary>
        /// Country code restriction.
        /// </summary>
        [JsonPropertyName("country")]
        public string? Country { get; set; }

        /// <summary>
        /// Configuration file URL for the player.
        /// </summary>
        [JsonPropertyName("configFile")]
        public string? ConfigFile { get; set; }
    }

    /// <summary>
    /// Custom JSON converter for year fields that may be strings or numbers.
    /// Sprint 101A-02: Handles flexible year formats.
    /// </summary>
    internal class YearConverter : JsonConverter<int?>
    {
        public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    return reader.GetInt32();

                case JsonTokenType.String:
                    var str = reader.GetString();
                    if (string.IsNullOrEmpty(str)) return null;
                    return int.TryParse(str, out var year) ? year : null;

                default:
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
                writer.WriteNumberValue(value.Value);
            else
                writer.WriteNullValue();
        }
    }

    /// <summary>
    /// Extension methods for converting between JsonElement and AioMetaResponse.
    /// Sprint 101A-02: Provides backward compatibility for existing JsonElement-based code.
    /// </summary>
    public static class AioMetaExtensions
    {
        /// <summary>
        /// Converts a JsonElement to AioMetaResponse.
        /// Returns null if deserialization fails.
        /// </summary>
        public static AioMetaResponse? ToAioMetaResponse(this JsonElement element)
        {
            try
            {
                return element.Deserialize<AioMetaResponse>();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Safely extracts a string value from AioMeta.
        /// </summary>
        public static string? GetSafeString(this AioMeta? meta, Func<AioMeta, string?> selector)
        {
            return meta == null ? null : selector(meta);
        }

        /// <summary>
        /// Safely extracts a string array value from AioMeta.
        /// </summary>
        public static List<string>? GetSafeArray(this AioMeta? meta, Func<AioMeta, List<string>?> selector)
        {
            return meta == null ? null : selector(meta) ?? new List<string>();
        }
    }
}
