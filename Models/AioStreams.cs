using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InfiniteDrive.Services
{
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
        /// InfiniteDrive does not use this — it is stored for diagnostic purposes only.
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
        /// InfiniteDrive library from Trakt or MDBList instead.
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
        /// which is the only ID scheme InfiniteDrive currently generates.
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
        /// InfiniteDrive uses this as a floor for <c>SyncResolveTimeoutSeconds</c>.
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
        /// any useful results.  InfiniteDrive warns the admin if this is true and
        /// the plugin has not been configured.
        /// </summary>
        [JsonPropertyName("configurationRequired")] public bool? ConfigurationRequired { get; set; }
    }

    /// <summary>
    /// <summary>
    /// Stremio addons configuration block present on some hosted AIOStreams
    /// instances (e.g. ElfHosted).  Used by Stremio clients to verify addon
    /// identity via a signed JWT.  InfiniteDrive does not act on this.
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

        /// <summary>Logo image URL.</summary>
        [JsonPropertyName("logo")]        public string? Logo        { get; set; }

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

        /// <summary>Rich description from AIOStreams (e.g. "Title · Year · Size · Quality · Audio").</summary>
        [JsonPropertyName("description")]   public string? Description   { get; set; }

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

    // ── Exceptions ────────────────────────────────────────────────────────────

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
