using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbyStreams.Models;
using EmbyStreams.Resilience;
using Polly;

namespace EmbyStreams.Services
{
    // ── Stremio resources polymorphic converter ──────────────────────────────────
    // The Stremio spec allows "resources" to be either a plain string array
    // ["catalog","meta"] or an object array [{"name":"stream","types":["movie"]}].
    // Cinemeta uses the string form; AIOStreams uses the object form.
    // This converter handles both transparently.

    internal sealed class ResourceListConverter : JsonConverter<List<AioStreamsResource>>
    {
        public override List<AioStreamsResource> Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var list = new List<AioStreamsResource>();
            if (reader.TokenType != JsonTokenType.StartArray)
                return list;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    var name = reader.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        list.Add(new AioStreamsResource { Name = name });
                    // else: skip empty/null string entries in resources array
                }
                else if (reader.TokenType == JsonTokenType.StartObject)
                {
                    // NOTE: inner.Converters.Remove(this) is intentionally a no-op.
                    // 'inner' is a copy of options — Remove() compares by reference
                    // and finds no match. This is safe because System.Text.Json
                    // falls through to the default object deserializer for
                    // JsonTokenType.StartObject, avoiding recursion.
                    // Do NOT attempt to make this removal work — it would cause
                    // infinite recursion on object-form resource entries.
                    var inner = new JsonSerializerOptions(options);
                    // inner.Converters.Remove(this); // no-op by design — see above
                    var res = JsonSerializer.Deserialize<AioStreamsResource>(ref reader, inner)
                              ?? new AioStreamsResource();
                    list.Add(res);
                }
                else
                {
                    reader.Skip();
                }
            }
            return list;
        }

        public override void Write(
            Utf8JsonWriter writer, List<AioStreamsResource> value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value, options);
    }

    // ╔══════════════════════════════════════════════════════════════════════════╗
    // ║  AIOSTREAMS RESPONSE MODELS                                              ║
    // ╚══════════════════════════════════════════════════════════════════════════╝

    // ── Manifest ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Top-level AIOStreams manifest response.
    /// Follows the Stremio addon manifest specification.
    /// </summary>
    public class AioStreamsManifest
    {
        [JsonPropertyName("id")]           public string? Id           { get; set; }
        [JsonPropertyName("version")]      public string? Version      { get; set; }
        [JsonPropertyName("name")]         public string? Name         { get; set; }
        [JsonPropertyName("description")]  public string? Description  { get; set; }
        [JsonPropertyName("resources")]
        [JsonConverter(typeof(ResourceListConverter))]
        public List<AioStreamsResource>? Resources { get; set; }
        [JsonPropertyName("types")]        public List<string>? Types   { get; set; }
        [JsonPropertyName("idPrefixes")]   public List<string>? IdPrefixes { get; set; }
        [JsonPropertyName("catalogs")]     public List<AioStreamsCatalogDef>? Catalogs { get; set; }
        [JsonPropertyName("addonCatalogs")]       public List<AioStreamsCatalogDef>?   AddonCatalogs       { get; set; }

        /// <summary>
        /// Stremio addons configuration block, present on some hosted AIOStreams
        /// instances (e.g. ElfHosted).  Contains an <c>issuer</c> URL and a
        /// <c>signature</c> JWE used by Stremio clients to verify addon identity.
        /// EmbyStreams does not use this — it is stored for diagnostic purposes only.
        /// </summary>
        [JsonPropertyName("stremioAddonsConfig")] public StremioAddonsConfig?          StremioAddonsConfig { get; set; }

        /// <summary>
        /// Addon-level behaviour hints.  AIOStreams populates this with
        /// <c>requestTimeout</c> (how long to wait for stream resolution),
        /// <c>adult</c> (whether the instance serves adult content), and
        /// <c>p2p</c> (whether P2P/torrent streams are included).
        /// </summary>
        [JsonPropertyName("behaviorHints")]  public AioStreamsManifestBehaviorHints? BehaviorHints { get; set; }

        // ── Derived helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// True when the manifest exposes a <c>stream</c> resource but has
        /// no catalog entries.  Duck Streams and many private AIOStreams
        /// deployments are stream-only: they resolve streams on demand but
        /// publish no IMDB-browsable catalog, so the user must populate the
        /// EmbyStreams library from Trakt or MDBList instead.
        /// </summary>
        public bool IsStreamOnly =>
            HasStreamResource && (Catalogs == null || Catalogs.Count == 0);

        /// <summary>
        /// True when the manifest's <c>resources</c> array contains an entry
        /// named <c>stream</c> with at least one supported type.
        /// </summary>
        public bool HasStreamResource =>
            Resources?.Any(r =>
                string.Equals(r.Name, "stream", StringComparison.OrdinalIgnoreCase)
                && (r.Types?.Count ?? 0) > 0) ?? false;

        /// <summary>
        /// Returns the ID prefixes that the <c>stream</c> resource accepts,
        /// e.g. <c>["tt", "kitsu:", "mal:"]</c>.  Empty list = accepts any.
        /// </summary>
        public List<string> StreamIdPrefixes =>
            Resources?
                .FirstOrDefault(r => string.Equals(r.Name, "stream", StringComparison.OrdinalIgnoreCase))
                ?.IdPrefixes
            ?? new List<string>();

        /// <summary>
        /// True when the stream resource accepts <c>tt</c> (IMDB) IDs,
        /// which is the only ID scheme EmbyStreams currently generates.
        /// </summary>
        public bool SupportsImdbIds =>
            StreamIdPrefixes.Count == 0
            || StreamIdPrefixes.Any(p => p.Equals("tt", StringComparison.OrdinalIgnoreCase)
                                       || p.Equals("imdb", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A single catalog entry inside the AIOStreams manifest.
    /// One row per provider/type combination (e.g. "Torrentio – Movies").
    /// </summary>
    public class AioStreamsCatalogDef
    {
        /// <summary>Catalog identifier, e.g. <c>aiostreams</c>, <c>gdrive</c>, <c>torrentio_movies</c>.</summary>
        [JsonPropertyName("id")]    public string? Id    { get; set; }

        /// <summary>Human-readable name shown in the Stremio UI.</summary>
        [JsonPropertyName("name")]  public string? Name  { get; set; }

        /// <summary>Media type: <c>movie</c>, <c>series</c>, or <c>anime</c>.</summary>
        [JsonPropertyName("type")]  public string? Type  { get; set; }

        /// <summary>Optional extra parameters (search, genre, etc.).</summary>
        [JsonPropertyName("extra")] public List<AioStreamsCatalogExtra>? Extra { get; set; }
    }

    /// <summary>
    /// Extra parameter definition for a catalog (search, genre filter, etc.).
    /// </summary>
    public class AioStreamsCatalogExtra
    {
        [JsonPropertyName("name")]       public string? Name       { get; set; }
        [JsonPropertyName("isRequired")] public bool IsRequired    { get; set; }
        [JsonPropertyName("options")]    public List<string>? Options { get; set; }
        [JsonPropertyName("optionsLimit")] public int? OptionsLimit { get; set; }
    }

    /// <summary>
    /// Top-level <c>behaviorHints</c> object in the AIOStreams manifest.
    /// Present on AIOStreams instances running v1.3+.
    /// </summary>
    public class AioStreamsManifestBehaviorHints
    {
        /// <summary>
        /// How many seconds AIOStreams needs to resolve one stream request.
        /// Driven by the number and latency of addons the user has configured.
        /// EmbyStreams uses this as a floor for <c>SyncResolveTimeoutSeconds</c>.
        /// </summary>
        [JsonPropertyName("requestTimeout")] public int?  RequestTimeout { get; set; }

        /// <summary>True when this AIOStreams instance serves adult content.</summary>
        [JsonPropertyName("adult")]          public bool? Adult          { get; set; }

        /// <summary>True when this manifest exposes P2P / torrent streams.</summary>
        [JsonPropertyName("p2p")]            public bool? P2p            { get; set; }

        /// <summary>True when this addon has a Stremio configuration page.</summary>
        [JsonPropertyName("configurable")]         public bool? Configurable         { get; set; }
        /// <summary>True when this addon has a Stremio configuration page.</summary>

        /// <summary>
        /// True when the user must complete addon configuration before it returns
        /// any useful results.  EmbyStreams warns the admin if this is true and
        /// the plugin has not been configured.
        /// </summary>
        [JsonPropertyName("configurationRequired")] public bool? ConfigurationRequired { get; set; }
    }

    /// <summary>
    /// <summary>
    /// Stremio addons configuration block present on some hosted AIOStreams
    /// instances (e.g. ElfHosted).  Used by Stremio clients to verify addon
    /// identity via a signed JWT.  EmbyStreams does not act on this.
    /// </summary>
    public class StremioAddonsConfig
    {
        /// <summary>URL of the issuing authority, e.g. <c>https://stremio-addons.net</c>.</summary>
        [JsonPropertyName("issuer")]    public string? Issuer    { get; set; }

        /// <summary>JWE-encoded signed addon configuration blob.</summary>
        [JsonPropertyName("signature")] public string? Signature { get; set; }
    }

    /// <summary>
    /// One entry in the <c>resources</c> array of an AIOStreams manifest.
    /// Describes which Stremio resource (stream, catalog, meta, subtitles) the
    /// addon provides, and for which media types and ID-prefix schemes.
    /// </summary>
    public class AioStreamsResource
    {
        /// <summary>Resource name: <c>stream</c>, <c>catalog</c>, <c>meta</c>, or <c>subtitles</c>.</summary>
        [JsonPropertyName("name")]       public string?       Name       { get; set; }

        /// <summary>Media types this resource covers, e.g. <c>["movie","series","anime"]</c>.</summary>
        [JsonPropertyName("types")]      public List<string>? Types      { get; set; }

        /// <summary>
        /// ID-prefix schemes this resource understands, e.g. <c>["tt","tmdb:","mal:"]</c>.
        /// An empty list means the resource accepts any ID.
        /// </summary>
        [JsonPropertyName("idPrefixes")] public List<string>? IdPrefixes { get; set; }
    }

    // ── Catalog response ────────────────────────────────────────────────────────

    /// <summary>
    /// Response from a catalog endpoint: <c>/catalog/{type}/{id}.json</c>
    /// </summary>
    public class AioStreamsCatalogResponse
    {
        [JsonPropertyName("metas")] public List<AioStreamsMeta>? Metas { get; set; }
    }

    /// <summary>
    /// One media item inside a catalog response (Stremio Meta object).
    /// </summary>
    public class AioStreamsMeta
    {
        /// <summary>
        /// Unique identifier for this item.  For AIOStreams this is the IMDB ID
        /// (e.g. <c>tt1234567</c>).  Some addons (TMDB-based, Kitsu) use a
        /// different prefix.
        /// </summary>
        [JsonPropertyName("id")]          public string? Id          { get; set; }

        /// <summary>IMDB ID when separate from the primary id field.</summary>
        [JsonPropertyName("imdb_id")]     public string? ImdbId      { get; set; }

        /// <summary>Media type: <c>movie</c>, <c>series</c>, or <c>anime</c>.</summary>
        [JsonPropertyName("type")]        public string? Type        { get; set; }

        /// <summary>Display title.</summary>
        [JsonPropertyName("name")]        public string? Name        { get; set; }

        /// <summary>Release year or year-range string like "2022–" for ongoing series.</summary>
        [JsonPropertyName("releaseInfo")] public string? ReleaseInfo { get; set; }

        /// <summary>TMDB numeric ID as returned by some AIOStreams addons.</summary>
        [JsonPropertyName("tmdbId")]      public string? TmdbId      { get; set; }

        /// <summary>Alternative TMDB field name.</summary>
        [JsonPropertyName("tmdb_id")]     public string? TmdbIdAlt   { get; set; }

        /// <summary>Poster image URL.</summary>
        [JsonPropertyName("poster")]      public string? Poster      { get; set; }

        /// <summary>IMDB rating.</summary>
        [JsonPropertyName("imdbRating")]  public string? ImdbRating  { get; set; }

        /// <summary>Genre list.</summary>
        [JsonPropertyName("genres")]      public List<string>? Genres { get; set; }

        /// <summary>Background/hero image URL.</summary>
        [JsonPropertyName("background")]  public string? Background  { get; set; }

        /// <summary>Short description.</summary>
        [JsonPropertyName("description")] public string? Description { get; set; }

        /// <summary>Source addon ID (populated by AIOStreams internally).</summary>
        [JsonPropertyName("addon")]       public string? Addon       { get; set; }
    }

    // ── Stream response ─────────────────────────────────────────────────────────

    /// <summary>
    /// Response from a stream endpoint:
    /// <c>/stream/movie/{imdbId}.json</c> or
    /// <c>/stream/series/{imdbId}:{season}:{episode}.json</c>
    /// </summary>
    public class AioStreamsStreamResponse
    {
        [JsonPropertyName("streams")] public List<AioStreamsStream>? Streams { get; set; }
    }

    /// <summary>
    /// A single resolved stream returned by AIOStreams.
    ///
    /// AIOStreams has already applied all user-configured filters and sorting,
    /// so <c>streams[0]</c> is always the user's preferred stream.
    ///
    /// Fields reflect the full AIOStreams ParsedStream schema including debrid,
    /// usenet, torrent and HTTP stream types.
    /// </summary>
    public class AioStreamsStream
    {
        // ── Core ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Direct playable URL.  For debrid streams this is the Real-Debrid /
        /// AllDebrid / TorBox / Premiumize CDN link.  For usenet it may be a
        /// NZBDav / AltMount / Easynews direct-play URL.
        /// </summary>
        [JsonPropertyName("url")]           public string? Url           { get; set; }

        /// <summary>Human-readable title line shown in the Stremio stream picker.</summary>
        [JsonPropertyName("title")]         public string? Title         { get; set; }

        /// <summary>
        /// The stream name shown in the Stremio UI (typically contains quality info).
        /// </summary>
        [JsonPropertyName("name")]          public string? Name          { get; set; }

        /// <summary>YouTube video ID (for YouTube-sourced streams).</summary>
        [JsonPropertyName("ytId")]          public string? YtId          { get; set; }

        /// <summary>External URL (for streams requiring a separate player).</summary>
        [JsonPropertyName("externalUrl")]   public string? ExternalUrl   { get; set; }

        // ── Torrent fields ──────────────────────────────────────────────────────

        /// <summary>Info-hash for P2P / torrent streams (40-char hex).</summary>
        [JsonPropertyName("infoHash")]      public string? InfoHash      { get; set; }

        /// <summary>File index within the torrent archive.</summary>
        [JsonPropertyName("fileIdx")]       public int?    FileIdx       { get; set; }

        /// <summary>Tracker/source URLs for the torrent.</summary>
        [JsonPropertyName("sources")]       public List<string>? Sources { get; set; }

        // ── Behaviour hints ─────────────────────────────────────────────────────

        /// <summary>
        /// Behaviour hints from AIOStreams.  Contains the filename which is used
        /// to infer quality tier, codec, audio, and season pack status.
        /// </summary>
        [JsonPropertyName("behaviorHints")] public AioStreamsBehaviorHints? BehaviorHints { get; set; }

        // ── Parsed metadata (AIOStreams extensions) ──────────────────────────────

        /// <summary>
        /// Parsed file metadata injected by AIOStreams after its processing pipeline.
        /// Present when AIOStreams returns extended stream objects.
        /// </summary>
        [JsonPropertyName("parsedFile")]    public AioStreamsParsedFile? ParsedFile { get; set; }

        /// <summary>Service information (debrid provider + cache status).</summary>
        [JsonPropertyName("service")]       public AioStreamsServiceInfo? Service { get; set; }

        /// <summary>
        /// Unique stream identifier assigned by AIOStreams.
        /// Used for library refresh actions and deduplication.
        /// </summary>
        [JsonPropertyName("id")]            public string? Id            { get; set; }

        /// <summary>Source addon identifier (e.g. <c>torrentio</c>, <c>mediafusion</c>).</summary>
        [JsonPropertyName("addon")]         public string? Addon         { get; set; }

        /// <summary>
        /// Stream type as classified by AIOStreams:
        /// <c>debrid</c>, <c>torrent</c>, <c>usenet</c>, <c>http</c>, <c>live</c>.
        /// </summary>
        [JsonPropertyName("type")]          public string? StreamType    { get; set; }

        /// <summary>File size in bytes (if known).</summary>
        [JsonPropertyName("size")]          public long?   Size          { get; set; }

        /// <summary>Torrent age in days.</summary>
        [JsonPropertyName("age")]           public int?    Age           { get; set; }

        /// <summary>Torrent seeder count.</summary>
        [JsonPropertyName("seeders")]       public int?    Seeders       { get; set; }

        /// <summary>Calculated bitrate in bits per second.</summary>
        [JsonPropertyName("bitrate")]       public long?   Bitrate       { get; set; }

        /// <summary>Content duration in seconds.</summary>
        [JsonPropertyName("duration")]      public int?    Duration      { get; set; }

        /// <summary>Indexer name (Prowlarr / Torznab / Newznab).</summary>
        [JsonPropertyName("indexer")]       public string? Indexer       { get; set; }

        /// <summary>NZB file URL for usenet streams (NZBDav / AltMount / Easynews).</summary>
        [JsonPropertyName("nzbUrl")]        public string? NzbUrl        { get; set; }

        /// <summary>Binge-group identifier for automatic next-episode chaining.</summary>
        [JsonPropertyName("bingeGroup")]    public string? BingeGroup    { get; set; }

        /// <summary>
        /// Whether this stream is from the user's Library addon cache.
        /// Library streams are prioritised as already-verified downloads.
        /// </summary>
        [JsonPropertyName("library")]       public bool?   Library       { get; set; }

        /// <summary>
        /// True when the stream should bypass standard filters (e.g. a library item).
        /// </summary>
        [JsonPropertyName("passthrough")]   public bool?   Passthrough   { get; set; }

        /// <summary>Custom HTTP headers required to play this stream.</summary>
        [JsonPropertyName("headers")]       public System.Collections.Generic.Dictionary<string, string>? Headers { get; set; }

        /// <summary>Subtitle track list.</summary>
        [JsonPropertyName("subtitles")]     public List<AioStreamsSubtitle>? Subtitles { get; set; }

        /// <summary>Error information when AIOStreams could not resolve the stream.</summary>
        [JsonPropertyName("error")]         public AioStreamsStreamError? Error { get; set; }
    }

    /// <summary>
    /// Stremio behaviorHints object.  AIOStreams populates this with the
    /// original filename and file size from the upstream addon/debrid service.
    /// </summary>
    public class AioStreamsBehaviorHints
    {
        /// <summary>
        /// Original filename (e.g. <c>Movie.2021.2160p.REMUX.DTS-HD.mkv</c>).
        /// This is the primary source for quality tier parsing.
        /// </summary>
        [JsonPropertyName("filename")]         public string? Filename        { get; set; }

        /// <summary>File size in bytes.</summary>
        [JsonPropertyName("videoSize")]        public long?   VideoSize       { get; set; }

        /// <summary>Binge-watching group identifier.</summary>
        [JsonPropertyName("bingeGroup")]       public string? BingeGroup      { get; set; }

        /// <summary>If true, this stream is not directly playable in a browser.</summary>
        [JsonPropertyName("notWebReady")]      public bool?   NotWebReady     { get; set; }

        /// <summary>Country whitelist for geo-restricted content.</summary>
        [JsonPropertyName("countryWhitelist")] public List<string>? CountryWhitelist { get; set; }

        /// <summary>Content hash for deduplication.</summary>
        [JsonPropertyName("videoHash")]        public string? VideoHash       { get; set; }

        /// <summary>Custom HTTP headers to attach when playing this stream.</summary>
        [JsonPropertyName("headers")]          public System.Collections.Generic.Dictionary<string, string>? Headers { get; set; }
    }

    /// <summary>
    /// Rich parsed-file metadata computed by AIOStreams' processing pipeline.
    /// Present on streams that have passed through AIOStreams' parser.
    /// </summary>
    public class AioStreamsParsedFile
    {
        /// <summary>Video resolution: <c>4K</c>, <c>1080p</c>, <c>720p</c>, etc.</summary>
        [JsonPropertyName("resolution")]   public string? Resolution  { get; set; }

        /// <summary>Quality tier: <c>Bluray</c>, <c>WEBRip</c>, <c>DVDRip</c>, etc.</summary>
        [JsonPropertyName("quality")]      public string? Quality     { get; set; }

        /// <summary>Video codec/encoding: <c>x265</c>, <c>x264</c>, <c>AV1</c>, etc.</summary>
        [JsonPropertyName("encode")]       public string? Encode      { get; set; }

        /// <summary>Audio tags: <c>Atmos</c>, <c>DTS-HD</c>, <c>FLAC</c>, etc.</summary>
        [JsonPropertyName("audioTags")]    public List<string>? AudioTags   { get; set; }

        /// <summary>Visual tags: <c>HDR</c>, <c>HDR10+</c>, <c>DV</c>, <c>10-bit</c>, etc.</summary>
        [JsonPropertyName("visualTags")]   public List<string>? VisualTags  { get; set; }

        /// <summary>ISO 639-1 language codes of audio tracks.</summary>
        [JsonPropertyName("languages")]    public List<string>? Languages   { get; set; }

        /// <summary>Audio channel configuration: <c>5.1</c>, <c>7.1</c>, <c>2.0</c>, etc.</summary>
        [JsonPropertyName("channels")]     public string? Channels    { get; set; }

        /// <summary>Scene release group name.</summary>
        [JsonPropertyName("releaseGroup")] public string? ReleaseGroup { get; set; }

        /// <summary>Human-readable title synthesised from the above fields.</summary>
        [JsonPropertyName("title")]        public string? Title        { get; set; }

        /// <summary>True if AIOStreams classified this as a season pack.</summary>
        [JsonPropertyName("seasonPack")]   public bool?   SeasonPack  { get; set; }
    }

    /// <summary>
    /// Debrid / streaming service context attached to a resolved stream.
    /// </summary>
    public class AioStreamsServiceInfo
    {
        /// <summary>
        /// Service identifier.
        /// Known values: <c>realdebrid</c>, <c>alldebrid</c>, <c>torbox</c>,
        /// <c>premiumize</c>, <c>debridlink</c>, <c>stremthru</c>,
        /// <c>nzbdav</c>, <c>altmount</c>, <c>easynews</c>, <c>stremio_nntp</c>.
        /// </summary>
        [JsonPropertyName("id")]     public string? Id     { get; set; }

        /// <summary>
        /// True when the content is already cached in the debrid service's CDN.
        /// Cached streams play instantly without torrent download wait.
        /// </summary>
        [JsonPropertyName("cached")] public bool?   Cached { get; set; }
    }

    /// <summary>
    /// Subtitle entry inside a stream object.
    /// </summary>
    public class AioStreamsSubtitle
    {
        [JsonPropertyName("id")]   public string? Id   { get; set; }
        [JsonPropertyName("url")]  public string? Url  { get; set; }
        [JsonPropertyName("lang")] public string? Lang { get; set; }
    }

    /// <summary>
    /// Error details returned by AIOStreams when stream resolution fails.
    /// </summary>
    public class AioStreamsStreamError
    {
        [JsonPropertyName("title")]       public string? Title       { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }

    // ╔══════════════════════════════════════════════════════════════════════════╗
    // ║  AIOSTREAMS CLIENT                                                       ║
    // ╚══════════════════════════════════════════════════════════════════════════╝

    /// <summary>
    /// Centralised HTTP client for all communication with an AIOStreams instance.
    ///
    /// Handles both authenticated and unauthenticated URL formats:
    /// <list type="bullet">
    ///   <item>Unauthenticated: <c>{base}/stremio/{resource}</c></item>
    ///   <item>Authenticated:   <c>{base}/stremio/{uuid}/{token}/{resource}</c></item>
    /// </list>
    ///
    /// All public methods accept a <see cref="CancellationToken"/> and log errors
    /// via the supplied <see cref="ILogger"/> rather than throwing.
    /// </summary>
    public class AioStreamsClient : IManifestProvider
    {
        // ── Constants ───────────────────────────────────────────────────────────

        private const string UserAgent     = "EmbyStreams/1.0 (+https://github.com/OneHotTake/embyStreams)";
        private const int    TimeoutSeconds = 60;  // Increased from 30s to handle slow AIOStreams responses (10+ seconds)

        // ── Fields ──────────────────────────────────────────────────────────────

        private static readonly JsonSerializerOptions _jsonOptions =
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // File-scoped static — one socket pool for all AioStreamsClient
        // instances. HttpClient is thread-safe and designed for reuse.
        private static readonly HttpClient _sharedHttp = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds)
        };

        // Polly resilience policy for AIOStreams HTTP calls (Sprint 104C-03)
        // Note: Policy is created per instance to use instance logger
        private readonly AsyncPolicy<HttpResponseMessage> _resiliencePolicy;

        static AioStreamsClient()
        {
            _sharedHttp.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", UserAgent);
        }

        private AsyncPolicy<HttpResponseMessage> CreateResiliencePolicy()
        {
            return AIOStreamsResiliencePolicy.CreatePolicy(_logger);
        }

        private readonly ILogger  _logger;
        private readonly string   _stremioBase;
        private readonly string?  _rawToken;     // stored for log sanitization only

        // ── Constructor ─────────────────────────────────────────────────────────

        /// <summary>
        /// Builds the client from a <see cref="PluginConfiguration"/>.
        /// Parses <see cref="PluginConfiguration.PrimaryManifestUrl"/> first,
        /// falling back to <see cref="PluginConfiguration.SecondaryManifestUrl"/> if needed.
        /// </summary>
        public AioStreamsClient(PluginConfiguration config, ILogger logger)
        {
            _logger = logger;
            _resiliencePolicy = CreateResiliencePolicy();

            // Attempt to parse the primary manifest URL.
            var (baseUrl, uuid, token) = TryParseManifestUrl(config.PrimaryManifestUrl);

            // Fall back to secondary manifest URL if primary is not provided.
            if (string.IsNullOrWhiteSpace(baseUrl))
                (baseUrl, uuid, token) = TryParseManifestUrl(config.SecondaryManifestUrl);

            // Build the base Stremio path segment.
            _stremioBase = BuildStremioBase(baseUrl, uuid, token);
            _rawToken    = string.IsNullOrWhiteSpace(token) ? null : token;
        }

        /// <summary>
        /// Direct constructor for when base URL, UUID, and token are already known.
        /// Used by tests and the dashboard health check endpoint.
        /// </summary>
        public AioStreamsClient(string baseUrl, string? uuid, string? token, ILogger logger)
        {
            _logger      = logger;
            _resiliencePolicy = CreateResiliencePolicy();
            _stremioBase = BuildStremioBase(baseUrl.TrimEnd('/'), uuid, token);
            _rawToken    = string.IsNullOrWhiteSpace(token) ? null : token;
        }

        /// <summary>
        /// Direct constructor for standard Stremio addons whose manifest lives at
        /// <c>{stremioBase}/manifest.json</c> with NO additional <c>/stremio/</c>
        /// path segment (e.g. Cinemeta: <c>https://v3-cinemeta.strem.io</c>).
        ///
        /// Use this instead of <see cref="AioStreamsClient(string,string?,string?,ILogger)"/>
        /// when the caller already knows the exact Stremio base path.
        /// </summary>
        public static AioStreamsClient CreateForStremioBase(string stremioBase, ILogger logger)
        {
            return new AioStreamsClient(stremioBase, logger);
        }

        // Private constructor used by CreateForStremioBase — sets _stremioBase directly.
        private AioStreamsClient(string directStremioBase, ILogger logger)
        {
            _logger      = logger;
            _stremioBase = directStremioBase.TrimEnd('/');
            _rawToken    = null;
        }

        // ── Public URL properties ───────────────────────────────────────────────

        /// <summary>
        /// The fully-qualified manifest URL for this AIOStreams instance.
        /// Useful for connection testing and display in the health dashboard.
        /// </summary>
        public string ManifestUrl => $"{_stremioBase}/manifest.json";

        /// <summary>
        /// Returns true when the client has a non-empty base URL.
        /// </summary>
        public bool IsConfigured => !string.IsNullOrEmpty(_stremioBase);

        // ── Manifest ────────────────────────────────────────────────────────────

        /// <summary>
        /// Fetches and parses the AIOStreams manifest.
        /// Returns null on any error (connectivity, auth, parse failure).
        /// </summary>
        public async Task<AioStreamsManifest?> GetManifestAsync(
            CancellationToken cancellationToken = default)
        {
            return await GetJsonAsync<AioStreamsManifest>(ManifestUrl, cancellationToken);
        }

        // ── Catalogs ────────────────────────────────────────────────────────────

        /// <summary>
        /// Fetches a catalog page from AIOStreams.
        /// </summary>
        /// <param name="type">Media type: <c>movie</c>, <c>series</c>, or <c>anime</c>.</param>
        /// <param name="catalogId">Catalog identifier from the manifest.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task<AioStreamsCatalogResponse?> GetCatalogAsync(
            string type,
            string catalogId,
            CancellationToken cancellationToken = default)
        {
            var url = $"{_stremioBase}/catalog/{type}/{Uri.EscapeDataString(catalogId)}.json";
            return await GetJsonAsync<AioStreamsCatalogResponse>(url, cancellationToken);
        }

        /// <summary>
        /// Fetches a catalog page with optional extra parameters (genre, search query, skip).
        /// </summary>
        public async Task<AioStreamsCatalogResponse?> GetCatalogAsync(
            string type,
            string catalogId,
            string? searchQuery,
            string? genre,
            int? skip,
            CancellationToken cancellationToken = default)
        {
            // Build extra segment following the Stremio extra-params convention:
            //   /catalog/{type}/{id}/{extra1=val1}&{extra2=val2}.json
            var extras = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(searchQuery))
                Append(extras, $"search={Uri.EscapeDataString(searchQuery)}");
            if (!string.IsNullOrEmpty(genre))
                Append(extras, $"genre={Uri.EscapeDataString(genre)}");
            if (skip.HasValue && skip.Value > 0)
                Append(extras, $"skip={skip.Value}");

            var url = extras.Length > 0
                ? $"{_stremioBase}/catalog/{type}/{Uri.EscapeDataString(catalogId)}/{extras}.json"
                : $"{_stremioBase}/catalog/{type}/{Uri.EscapeDataString(catalogId)}.json";

            return await GetJsonAsync<AioStreamsCatalogResponse>(url, cancellationToken);
        }

        // ── Streams ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Fetches streams for a movie by IMDB ID.
        /// AIOStreams returns streams sorted by user preference; always use index 0.
        /// </summary>
        /// <param name="imdbId">IMDB identifier, e.g. <c>tt1160419</c>.</param>
        public async Task<AioStreamsStreamResponse?> GetMovieStreamsAsync(
            string imdbId,
            CancellationToken cancellationToken = default)
        {
            var path = $"/stream/movie/{Uri.EscapeDataString(imdbId)}.json";
            var result = await GetJsonWithFallbackAsync<AioStreamsStreamResponse>(path, cancellationToken);
            // Sprint 100A-05: Error stub detection
            return CheckForErrorStub(result, imdbId);
        }

        /// <summary>
        /// Fetches streams for a TV episode.
        /// AIOStreams returns streams sorted by user preference; always use index 0.
        /// Tries the primary AIOStreams instance first; on connection failure or
        /// timeout falls through to each configured fallback URL in order.
        /// </summary>
        /// <param name="imdbId">IMDB identifier, e.g. <c>tt0903747</c>.</param>
        /// <param name="season">Season number (1-based).</param>
        /// <param name="episode">Episode number (1-based).</param>
        public async Task<AioStreamsStreamResponse?> GetSeriesStreamsAsync(
            string imdbId,
            int season,
            int episode,
            CancellationToken cancellationToken = default)
        {
            var id   = $"{imdbId}:{season}:{episode}";
            var path = $"/stream/series/{Uri.EscapeDataString(id)}.json";
            var result = await GetJsonWithFallbackAsync<AioStreamsStreamResponse>(path, cancellationToken);
            // Sprint 100A-05: Error stub detection
            return CheckForErrorStub(result, id);
        }

        // ── Error stub detection (Sprint 100A-05) ────────────────────────

        /// <summary>
        /// Detects AIOStreams error stub responses and returns empty list if found.
        /// Error stubs have title containing "error" or name containing "[AIOStreams]".
        /// </summary>
        private AioStreamsStreamResponse? CheckForErrorStub(
            AioStreamsStreamResponse? response,
            string itemId)
        {
            if (response?.Streams == null || response.Streams.Count == 0)
                return response;

            var firstStream = response.Streams[0];
            var title = firstStream.Title ?? string.Empty;
            var name = firstStream.Name ?? string.Empty;

            if (title.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("[AIOStreams]", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "[EmbyStreams] Error stub detected for item {Item}: Title='{Title}', Name='{Name}'. " +
                    "Treating as resolution failure.",
                    itemId, title, name);
                return new AioStreamsStreamResponse { Streams = new List<AioStreamsStream>() };
            }

            return response;
        }

        // ── Metadata ────────────────────────────────────────────────────────────

        /// <summary>
        /// Fetches metadata for a single item (poster, genres, description, etc.).
        /// Only available on AIOStreams instances that have the meta resource enabled.
        /// </summary>
        public async Task<JsonElement?> GetMetaAsync(
            string type,
            string id,
            CancellationToken cancellationToken = default)
        {
            var url = $"{_stremioBase}/meta/{type}/{Uri.EscapeDataString(id)}.json";
            return await GetJsonElementAsync(url, cancellationToken);
        }

        /// <summary>
        /// Fetches strongly-typed metadata for a single item.
        /// Sprint 101A-02: AIOMetadata deserialization.
        /// Returns null if deserialization fails or response is invalid.
        /// </summary>
        public async Task<AioMetaResponse?> GetMetaAsyncTyped(
            string type,
            string id,
            CancellationToken cancellationToken = default)
        {
            var url = $"{_stremioBase}/meta/{type}/{Uri.EscapeDataString(id)}.json";
            var json = await GetJsonAsync(url, cancellationToken);

            if (string.IsNullOrEmpty(json))
                return null;

            try
            {
                return JsonSerializer.Deserialize<AioMetaResponse>(json, _jsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(
                    ex,
                    "[AioStreamsClient] Failed to deserialize metadata for {Type} {Id}",
                    type, id);
                return null;
            }
        }

        // ── Connection health ────────────────────────────────────────────────────

        /// <summary>
        /// Maps exception types and HTTP status codes to user-friendly error messages.
        /// Used by TestConnectionAsync to provide clear UI feedback.
        /// </summary>
        private static string MapErrorToFriendlyMessage(Exception ex)
        {
            return ex switch
            {
                TaskCanceledException
                    => "Connection timed out. Is your provider reachable?",
                HttpRequestException
                    => "Could not reach the server. Check your network connection.",
                _ => ex.Message
            };
        }

        /// <summary>
        /// Maps HTTP status codes to user-friendly error messages.
        /// </summary>
        private static string MapHttpStatusCodeToMessage(int code)
        {
            return code switch
            {
                401 or 403
                    => "Authentication failed. Check your manifest token.",
                404
                    => "Manifest URL not found. Verify the URL is correct.",
                >= 500 and <= 599
                    => "Provider returned a server error. Try again shortly.",
                _
                    => $"Server returned HTTP {code}. Check the URL and try again."
            };
        }

        /// <summary>
        /// Tests connectivity by fetching the manifest.
        /// Returns <c>(true, null)</c> on success or <c>(false, errorMessage)</c>.
        /// </summary>
        public async Task<(bool Ok, string? Error)> TestConnectionAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                var manifest = await GetManifestAsync(cancellationToken);
                if (manifest?.Id != null)
                    return (true, null);

                return (false, "Manifest fetched but contained no ID field — check UUID/token");
            }
            catch (OperationCanceledException)
            {
                return (false, "Connection timed out. Is your provider reachable?");
            }
            catch (HttpRequestException ex)
            {
                return (false, MapErrorToFriendlyMessage(ex));
            }
            catch (Exception ex)
            {
                // Log the full exception for debugging, but return a clean message to UI
                return (false, $"Unexpected error: {ex.Message}");
            }
        }

        // ── Utility: URL builders (public for external use) ──────────────────────

        /// <summary>
        /// Returns the stream URL for a movie without making an HTTP call.
        /// Useful for building .strm file content and debug logging.
        /// </summary>
        public string GetMovieStreamUrl(string imdbId)
            => $"{_stremioBase}/stream/movie/{Uri.EscapeDataString(imdbId)}.json";

        /// <summary>
        /// Returns the stream URL for a TV episode without making an HTTP call.
        /// </summary>
        public string GetSeriesStreamUrl(string imdbId, int season, int episode)
            => $"{_stremioBase}/stream/series/{Uri.EscapeDataString($"{imdbId}:{season}:{episode}")}.json";

        /// <summary>
        /// ── FIX-100B-05: Kitsu/AniList absolute episode numbering ────
        /// Returns the stream URL for an anime episode using absolute episode numbering.
        /// Stream ID format: {provider}:{seriesId}:{absoluteEpisode}
        /// Supported providers: kitsu, anilist
        /// </summary>
        /// <param name="provider">Provider prefix: "kitsu" or "anilist".</param>
        /// <param name="seriesId">Series ID from the provider.</param>
        /// <param name="absoluteEpisode">Absolute episode number across all seasons.</param>
        public string GetAnimeStreamUrl(string provider, string seriesId, int absoluteEpisode)
            => $"{_stremioBase}/stream/series/{Uri.EscapeDataString($"{provider}:{seriesId}:{absoluteEpisode}")}.json";

        /// <summary>
        /// ── FIX-100B-05: Absolute episode calculation ────────────────────
        /// Calculates the absolute episode number for anime series.
        /// Absolute episode = sum of episodes in previous seasons + current episode.
        /// Uses 12 as the default episode count estimate for unknown season lengths.
        /// </summary>
        /// <param name="season">Current season (1-based).</param>
        /// <param name="episode">Current episode within the season (1-based).</param>
        /// <param name="previousSeasonCounts">Array of episode counts for seasons before current (index 0 = season 1).</param>
        /// <returns>Absolute episode number.</returns>
        public static int CalculateAbsoluteEpisode(
            int season,
            int episode,
            int[]? previousSeasonCounts = null)
        {
            if (season < 1) season = 1;
            if (episode < 1) episode = 1;

            int absolute = 0;

            // Add up episodes from all previous seasons
            if (previousSeasonCounts != null)
            {
                for (int s = 0; s < Math.Min(season - 1, previousSeasonCounts.Length); s++)
                {
                    absolute += previousSeasonCounts[s];
                }
            }
            else
            {
                // Default estimate: 12 episodes per season for unknown seasons
                absolute += (season - 1) * 12;
            }

            // Add current episode
            absolute += episode;

            return absolute;
        }

        /// <summary>
        /// Returns the catalog URL for a given type and catalog ID.
        /// </summary>
        public string GetCatalogUrl(string type, string catalogId)
            => $"{_stremioBase}/catalog/{type}/{Uri.EscapeDataString(catalogId)}.json";

        // ── Static helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Parses base URL, UUID, and token out of a manifest URL.
        /// Supports the patterns:
        /// <c>{base}/stremio/{uuid}/{token}/manifest.json</c>
        /// <c>{base}/stremio/manifest.json</c>
        /// </summary>
        public static (string BaseUrl, string Uuid, string Token) TryParseManifestUrl(
            string? manifestUrl)
        {
            if (string.IsNullOrWhiteSpace(manifestUrl))
                return (string.Empty, string.Empty, string.Empty);

            try
            {
                var uri     = new Uri(manifestUrl);
                var segs    = uri.AbsolutePath.TrimStart('/').Split('/');
                // Expected: stremio / {uuid} / {token} / manifest.json   (len ≥ 4)
                //       or: stremio / manifest.json                       (len = 2)
                if (segs.Length >= 4
                    && string.Equals(segs[0], "stremio", StringComparison.OrdinalIgnoreCase))
                {
                    var baseUrl = $"{uri.Scheme}://{uri.Authority}";
                    return (baseUrl, segs[1], segs[2]);
                }

                if (segs.Length >= 2
                    && string.Equals(segs[0], "stremio", StringComparison.OrdinalIgnoreCase))
                {
                    var baseUrl = $"{uri.Scheme}://{uri.Authority}";
                    return (baseUrl, string.Empty, string.Empty);
                }

                // Plain manifest URL: {base}/manifest.json or {base}/some/path/manifest.json
                // e.g. https://v3-cinemeta.strem.io/manifest.json
                // Use "DIRECT" sentinel so BuildStremioBase returns the base without appending /stremio
                if (manifestUrl.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase)
                    || manifestUrl.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    var stremioBase = manifestUrl.Substring(0, manifestUrl.LastIndexOf("/manifest.json", StringComparison.OrdinalIgnoreCase));
                    return (stremioBase, "DIRECT", string.Empty);
                }
            }
            catch
            {
                // Malformed URL — caller will fall back to individual fields
            }

            return (string.Empty, string.Empty, string.Empty);
        }

        // ── IDisposable ──────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public void Dispose()
        {
            // _sharedHttp is static — not disposed per instance.
        }

        // ── Private: HTTP helpers ────────────────────────────────────────────────

        /// <summary>
        /// Fetches JSON from the AIOStreams instance for the given relative path.
        ///
        /// On connection failure or timeout, throws <see cref="AioStreamsUnreachableException"/>
        /// so that PlaybackService can try the next provider in the list.
        ///
        /// On HTTP 4xx/5xx response from a reachable server, returns null (application error,
        /// not a network error, so don't retry on another provider).
        /// </summary>
        private async Task<T?> GetJsonWithFallbackAsync<T>(
            string relativePath, CancellationToken cancellationToken) where T : class
        {
            var fullUrl = _stremioBase + relativePath;
            var result = await GetJsonAsync<T>(fullUrl, cancellationToken, throwOnUnreachable: true);
            return result;
        }

        /// <summary>
        /// Fetches a URL and deserialises the response.
        /// Always throws <see cref="AioStreamsRateLimitException"/> on HTTP 429.
        /// When <paramref name="throwOnUnreachable"/> is <c>true</c>, throws
        /// <see cref="AioStreamsUnreachableException"/> on connection failure or
        /// timeout so <see cref="GetJsonWithFallbackAsync{T}"/> can retry on a
        /// fallback instance.  When <c>false</c> (default, used by manifest/catalog
        /// callers), returns null on any network failure — preserving the original
        /// return-null-on-error contract.
        /// </summary>
        private async Task<T?> GetJsonAsync<T>(
            string url,
            CancellationToken cancellationToken,
            bool throwOnUnreachable = false) where T : class
        {
            var safeUrl = SanitizeUrl(url);
            // Sprint 100A-11: Inline retry with exponential backoff
            int maxAttempts = 3;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    _logger.LogDebug("[EmbyStreams] GET {Url} (attempt {Attempt}/{Max})",
                        safeUrl, attempt, maxAttempts);

                    // Apply Polly resilience policy for timeout and circuit breaker (Sprint 104C-03)
                    var response = await _resiliencePolicy.ExecuteAsync(async ct =>
                        await _sharedHttp.GetAsync(url, ct), cancellationToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        var code = (int)response.StatusCode;

                        // Sprint 100A-11: Do NOT retry 401, 403, 404
                        if (code == 401 || code == 403 || code == 404)
                        {
                            _logger.LogDebug("[EmbyStreams] {Code} from AIOStreams: {Url} — not retrying",
                                code, safeUrl);
                            return null;
                        }

                        if (code == 429)
                            throw new AioStreamsRateLimitException(safeUrl);
                        if (code == 404)
                            _logger.LogDebug("[EmbyStreams] 404 from AIOStreams: {Url}", safeUrl);
                        else
                            _logger.LogWarning(
                                "[EmbyStreams] AIOStreams returned {Status} for {Url}", code, safeUrl);
                        return null;
                    }

                    var json = await response.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<T>(json, _jsonOptions);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (TaskCanceledException ex)
                {
                    _logger.LogWarning("[EmbyStreams] Timeout fetching {Url}: {Msg}",
                        safeUrl, ex.Message);

                    // Sprint 100A-11: Retry on timeout with delays: 1s, 4s, 16s
                    if (attempt < maxAttempts)
                    {
                        int delayMs = attempt == 1 ? 1000 : (attempt == 2 ? 4000 : 16000);
                        _logger.LogWarning(
                            "[EmbyStreams] Retrying {Url} in {DelayMs}ms (attempt {Attempt}/{Max})",
                            safeUrl, delayMs, attempt, maxAttempts);
                        await Task.Delay(delayMs, cancellationToken);
                        continue;
                    }

                    if (throwOnUnreachable) throw new AioStreamsUnreachableException(safeUrl, null);
                    return null;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning("[EmbyStreams] Connection failed for {Url}: {Msg}",
                        safeUrl, ex.Message);

                    // Sprint 100A-11: Retry on HttpRequestException
                    if (attempt < maxAttempts)
                    {
                        int delayMs = attempt == 1 ? 1000 : (attempt == 2 ? 4000 : 16000);
                        _logger.LogWarning(
                            "[EmbyStreams] Retrying {Url} in {DelayMs}ms (attempt {Attempt}/{Max})",
                            safeUrl, delayMs, attempt, maxAttempts);
                        await Task.Delay(delayMs, cancellationToken);
                        continue;
                    }

                    if (throwOnUnreachable) throw new AioStreamsUnreachableException(safeUrl, ex);
                    return null;
                }
                catch (AioStreamsRateLimitException) { throw; }
                catch (AioStreamsUnreachableException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[EmbyStreams] Error fetching {Url}", safeUrl);
                    return null;
                }
            }

            return null;
        }

        private async Task<JsonElement?> GetJsonElementAsync(
            string url, CancellationToken cancellationToken)
        {
            var safeUrl = SanitizeUrl(url);
            try
            {
                _logger.LogDebug("[EmbyStreams] GET {Url}", safeUrl);
                // Apply Polly resilience policy for timeout and circuit breaker (Sprint 104C-03)
                var response = await _resiliencePolicy.ExecuteAsync(async ct =>
                    await _sharedHttp.GetAsync(url, ct), cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var code = (int)response.StatusCode;
                    if (code == 429) throw new AioStreamsRateLimitException(safeUrl);
                    _logger.LogDebug("[EmbyStreams] {Status} from AIOStreams: {Url}", code, safeUrl);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.Clone();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (TaskCanceledException)
            {
                _logger.LogWarning("[EmbyStreams] Timeout fetching {Url}", safeUrl);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EmbyStreams] Error fetching {Url}", safeUrl);
                return null;
            }
        }

        /// <summary>
        /// Fetches raw JSON string from a URL.
        /// Sprint 101A-02: Used for typed metadata deserialization.
        /// Returns null on error.
        /// </summary>
        private async Task<string?> GetJsonAsync(
            string url,
            CancellationToken cancellationToken)
        {
            var safeUrl = SanitizeUrl(url);
            try
            {
                _logger.LogDebug("[EmbyStreams] GET {Url}", safeUrl);
                // Apply Polly resilience policy for timeout and circuit breaker (Sprint 104C-03)
                var response = await _resiliencePolicy.ExecuteAsync(async ct =>
                    await _sharedHttp.GetAsync(url, ct), cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var code = (int)response.StatusCode;
                    if (code == 429) throw new AioStreamsRateLimitException(safeUrl);
                    _logger.LogDebug("[EmbyStreams] {Status} from AIOStreams: {Url}", code, safeUrl);
                    return null;
                }

                return await response.Content.ReadAsStringAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (TaskCanceledException)
            {
                _logger.LogWarning("[EmbyStreams] Timeout fetching {Url}", safeUrl);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EmbyStreams] Error fetching {Url}", safeUrl);
                return null;
            }
        }

        private static string BuildStremioBase(string baseUrl, string? uuid, string? token)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                return string.Empty;

            // "DIRECT" sentinel: baseUrl is already the full stremio base (no /stremio suffix needed)
            if (string.Equals(uuid, "DIRECT", StringComparison.Ordinal))
                return baseUrl.TrimEnd('/');

            var hasAuth = !string.IsNullOrWhiteSpace(uuid)
                       && !string.IsNullOrWhiteSpace(token);

            return hasAuth
                ? $"{baseUrl}/stremio/{uuid}/{token}"
                : $"{baseUrl}/stremio";
        }

        /// <summary>
        /// SEC-8: Validates that a URL is safe to use as an AIOStreams endpoint.
        ///
        /// Rejects:
        /// <list type="bullet">
        ///   <item>Non-http/https schemes (<c>file://</c>, <c>gopher://</c>, <c>ftp://</c>, etc.)</item>
        ///   <item>APIPA link-local addresses (<c>169.254.x.x</c>) — AWS/Azure metadata endpoints live here</item>
        ///   <item>Malformed or non-absolute URIs</item>
        /// </list>
        ///
        /// Loopback addresses (127.x.x.x, ::1) are intentionally <em>allowed</em> because
        /// self-hosted AIOStreams running on the same host as Emby is a legitimate configuration.
        /// </summary>
        private static bool IsAllowedAioStreamsUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            // Only allow http and https
            if (!string.Equals(uri.Scheme, "http",  StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                return false;

            // Reject APIPA / link-local (169.254.0.0/16) — cloud metadata services live here
            var host = uri.Host;
            if (host.StartsWith("169.254.", StringComparison.Ordinal))
                return false;

            return true;
        }

        /// <summary>
        /// Returns a sanitized version of a URL safe for log output.
        /// Replaces the token segment in <c>/stremio/{uuid}/{token}/…</c>
        /// paths with <c>[token]</c> so credentials never appear in log files.
        /// </summary>
        private string SanitizeUrl(string url)
        {
            if (_rawToken == null || string.IsNullOrEmpty(url))
                return url;

            return url.Replace(_rawToken, "[token]");
        }

        private static void Append(System.Text.StringBuilder sb, string part)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(part);
        }
    }

    // ── Custom exceptions ───────────────────────────────────────────────────────

    /// <summary>
    /// Thrown by <see cref="AioStreamsClient"/> when AIOStreams returns HTTP 429
    /// (Too Many Requests).  Callers should back off and retry.
    /// </summary>
    public class AioStreamsRateLimitException : Exception
    {
        /// <summary>The URL that returned 429.</summary>
        public string Url { get; }

        /// <summary>Initialises a new rate-limit exception for the given URL.</summary>
        public AioStreamsRateLimitException(string url)
            : base($"AIOStreams rate-limited (429): {url}")
        {
            Url = url;
        }
    }

    /// <summary>
    /// Thrown by <see cref="AioStreamsClient"/> when an AIOStreams instance is
    /// unreachable (connection refused, DNS failure, or request timeout).
    ///
    /// Distinct from an HTTP error response (4xx/5xx from a reachable server) so
    /// callers can decide whether to try a configured fallback AIOStreams instance.
    /// </summary>
    public class AioStreamsUnreachableException : Exception
    {
        /// <summary>The URL that was attempted.</summary>
        public string Url { get; }

        /// <summary>Initialises a new unreachable exception for the given URL.</summary>
        public AioStreamsUnreachableException(string url, Exception? inner)
            : base($"AIOStreams unreachable: {url}", inner)
        {
            Url = url;
        }
    }
}
