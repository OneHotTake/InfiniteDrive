using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using MediaBrowser.Model.Plugins;

namespace InfiniteDrive
{
    /// <summary>
    /// All persisted settings for the InfiniteDrive plugin.
    /// Emby serialises this to {DataPath}/plugins/configurations/InfiniteDrive.xml.
    /// Every property must carry <see cref="DataMemberAttribute"/> to be persisted.
    ///
    /// ──────────────────────────────────────────────────────────────────────────
    /// HOW AIOSTREAMS AUTHENTICATION WORKS
    /// ──────────────────────────────────────────────────────────────────────────
    /// AIOStreams exposes Stremio-format APIs at two URL shapes:
    ///
    ///   Unauthenticated: {base}/stremio/manifest.json
    ///   Authenticated:   {base}/stremio/{uuid}/{token}/manifest.json
    ///
    /// Catalog and stream endpoints follow the same pattern:
    ///   {stremioBase}/catalog/{type}/{catalogId}.json
    ///   {stremioBase}/stream/{type}/{id}.json
    ///   {stremioBase}/stream/series/{imdbId}:{season}:{episode}.json
    ///
    /// Set <see cref="PrimaryManifestUrl"/> to the full manifest URL (with auth included if needed).
    /// Both values appear in the manifest URL shown in your AIOStreams web UI.
    ///
    /// ──────────────────────────────────────────────────────────────────────────
    /// WHAT LIVES IN AIOSTREAMS vs WHAT LIVES HERE
    /// ──────────────────────────────────────────────────────────────────────────
    /// AIOStreams handles everything about *how* streams are chosen:
    ///   • Which debrid services to use (Real-Debrid, AllDebrid, TorBox,
    ///     Premiumize, Debrid-Link, StremThru, NZBDav, AltMount, Easynews)
    ///   • Which addon providers to query (Torrentio, Comet, MediaFusion,
    ///     Torrent Galaxy, EZTV, Knaben, SeaDex, Prowlarr, Newznab,
    ///     Torznab, Google Drive, TorBox Search, Easynews Search, Library)
    ///   • Resolution, quality, language, codec, audio, HDR/DV filters
    ///   • Sorting priority and stream expression rules
    ///   • Title/year/season matching
    ///
    /// InfiniteDrive only needs to know *where* that AIOStreams instance lives.
    /// Configure all quality/filter preferences inside AIOStreams itself.
    /// </summary>
    [DataContract]
    public class PluginConfiguration : BasePluginConfiguration
    {
        // ╔══════════════════════════════════════════════════════════════════════╗
        // ║  AIOSTREAMS CONNECTION (SIMPLIFIED)                                  ║
        // ╚══════════════════════════════════════════════════════════════════════╝

        /// <summary>
        /// Full manifest URL of the primary AIOStreams instance.
        ///
        /// Paste the URL from your AIOStreams web UI → Configure section.
        /// Examples:
        ///   - Unauthenticated: http://192.168.1.100:7860/stremio/manifest.json
        ///   - Authenticated: http://192.168.1.100:7860/stremio/abc123/xyz789/manifest.json
        ///
        /// The plugin automatically extracts base URL, UUID, and token from this.
        /// </summary>
        [DataMember]
        public string PrimaryManifestUrl { get; set; } = string.Empty;

        /// <summary>
        /// Optional full manifest URL of a secondary (backup) AIOStreams instance.
        ///
        /// Used only when the primary instance is unreachable. Configure this
        /// if you have a backup AIOStreams server in a different location or with
        /// different providers for failover redundancy.
        ///
        /// Leave empty to use only the primary instance.
        /// </summary>
        [DataMember]
        public string SecondaryManifestUrl { get; set; } = string.Empty;

        // ╔══════════════════════════════════════════════════════════════════════╗
        // ║  CATALOG SYNC SELECTION                                              ║
        // ╚══════════════════════════════════════════════════════════════════════╝

        /// <summary>
        /// Enable fetching catalog items from AIOStreams catalog endpoints.
        /// When enabled, InfiniteDrive reads the AIOStreams manifest on each sync
        /// to discover all configured catalogs automatically.
        /// </summary>
        [DataMember]
        public bool EnableAioStreamsCatalog { get; set; } = true;

        /// <summary>
        /// Optional comma-separated list of AIOStreams catalog IDs to sync.
        ///
        /// Leave empty to sync <em>all</em> catalogs found in the AIOStreams manifest
        /// (recommended — this captures every addon and provider the user has configured).
        ///
        /// To restrict to specific catalogs, list their IDs exactly as they appear
        /// in the AIOStreams manifest, e.g.:
        /// <c>aiostreams,torrentio_movies,mediafusion_movies</c>
        ///
        /// Known catalog ID patterns from AIOStreams addons:
        /// <list type="bullet">
        ///   <item><c>aiostreams</c> — AIOStreams default catalog</item>
        ///   <item><c>gdrive</c> — Google Drive catalog</item>
        ///   <item><c>library</c> — Library addon catalog</item>
        ///   <item><c>torbox-search</c> — TorBox catalog</item>
        ///   <item>Plus any custom catalogs from Prowlarr, Torznab, Newznab, etc.</item>
        /// </list>
        /// </summary>
        [DataMember]
        public string AioStreamsCatalogIds { get; set; } = string.Empty;

        /// <summary>
        /// Stream types to accept from AIOStreams when resolving for playback.
        /// Comma-separated from: <c>debrid,torrent,usenet,http,live</c>.
        ///
        /// Default: <c>debrid</c> (Real-Debrid / AllDebrid / TorBox cached links).
        /// Add <c>usenet</c> to also accept Easynews / NZBDav / AltMount streams.
        /// Set to empty string to accept all types AIOStreams returns.
        /// </summary>
        [DataMember]
        public string AioStreamsAcceptedStreamTypes { get; set; } = "debrid";


        // ╔══════════════════════════════════════════════════════════════════════╗
        // ║  EMBY LOCAL ADDRESS                                                  ║
        // ╚══════════════════════════════════════════════════════════════════════╝

        /// <summary>
        /// The loopback URL Emby listens on.  Written verbatim into every .strm file.
        /// Emby clients request this URL, which the plugin intercepts and resolves.
        /// Default: <c>http://127.0.0.1:8096</c>
        /// </summary>
        [DataMember]
        public string EmbyBaseUrl { get; set; } = "http://127.0.0.1:8096";

        /// <summary>
        /// Emby API key for .strm file authentication.
        /// Used in .strm files to authenticate playback requests.
        ///
        /// Get from: Emby Dashboard → API Keys → Add → Copy the key
        /// Or use /InfiniteDrive/Setup/CreateEmbyApiKey to create one programmatically.
        ///
        /// WARNING: If leaked, this gives full access to your Emby server!
        /// Treat this like a password - never share it.
        /// </summary>
        [DataMember]
        public string EmbyApiKey { get; set; } = string.Empty;

        // ╔══════════════════════════════════════════════════════════════════════╗
        // ║  .STRM FILE STORAGE PATHS                                            ║
        // ╚══════════════════════════════════════════════════════════════════════╝

        /// <summary>
        /// Absolute path where movie .strm files are written.
        /// Emby should have a Movies library pointed at this folder.
        /// Default: <c>/media/embystreams/movies</c>
        /// </summary>
        [DataMember]
        public string SyncPathMovies { get; set; } = "/media/embystreams/movies";

        /// <summary>
        /// Absolute path where TV show .strm files are written.
        /// Emby should have a TV Shows library pointed at this folder.
        /// Default: <c>/media/embystreams/shows</c>
        /// </summary>
        [DataMember]
        public string SyncPathShows { get; set; } = "/media/embystreams/shows";

        /// <summary>Display name for the Movies library created by the plugin.</summary>
        [DataMember]
        public string LibraryNameMovies { get; set; } = "Streamed Movies";

        /// <summary>Display name for the Series library created by the plugin.</summary>
        [DataMember]
        public string LibraryNameSeries { get; set; } = "Streamed Series";

        /// <summary>Display name for the Anime library created by the plugin.</summary>
        [DataMember]
        public string LibraryNameAnime { get; set; } = "Streamed Anime";

        /// <summary>Number of days signed .strm URLs remain valid. Default: 365.</summary>
        [DataMember]
        public int SignatureValidityDays { get; set; } = 365;

        /// <summary>
        /// When <c>true</c>, <c>type: "anime"</c> items from AIOStreams are routed
        /// to a dedicated anime library.  When <c>false</c> (default), anime items
        /// are filtered out entirely during catalog sync.
        ///
        /// Requires the Emby Anime Plugin to be installed.
        /// </summary>
        [DataMember]
        public bool EnableAnimeLibrary { get; set; } = false;

        /// <summary>
        /// Absolute path where anime .strm files are written.
        /// InfiniteDrive creates a Series library at this path when anime is enabled.
        /// Default: <c>/media/embystreams/anime</c>
        /// </summary>
        [DataMember]
        public string SyncPathAnime { get; set; } = "/media/embystreams/anime";

        
        /// <summary>Skip episodes that haven't aired yet. Default: true.</summary>
        [DataMember]
        public bool SkipFutureEpisodes { get; set; } = true;

        /// <summary>Buffer days to consider future episodes as aired. Default: 2.</summary>
        [DataMember]
        public int FutureEpisodeBufferDays { get; set; } = 2;

        /// <summary>
        /// Default number of seasons to write when series metadata is unavailable.
        /// Used by SeriesPreExpansionService when Stremio metadata returns 404.
        /// Default: 1.
        /// </summary>
        [DataMember]
        public int DefaultSeriesSeasons { get; set; } = 1;

        /// <summary>
        /// Default number of episodes per season to write when series metadata is unavailable.
        /// Used by SeriesPreExpansionService when Stremio metadata returns 404.
        /// Default: 10.
        /// </summary>
        [DataMember]
        public int DefaultSeriesEpisodesPerSeason { get; set; } = 10;

        /// <summary>
        /// When <c>true</c> (default), InfiniteDrive writes a minimal Kodi-format
        /// <c>.nfo</c> file alongside every <c>.strm</c> file it creates.
        ///
        /// The <c>.nfo</c> contains only IMDB and TMDB <c>&lt;uniqueid&gt;</c> tags —
        /// no plot, poster, or cast data.  Emby reads these IDs to match the item
        /// against its internal scraper rather than relying solely on the filename.
        ///
        /// This improves metadata lookup reliability (especially for movies whose
        /// titles differ between Cinemeta and Emby's TMDB scraper) and eliminates
        /// the need for exact filename formatting.
        ///
        /// Disable only if another tool manages your <c>.nfo</c> files and you do
        /// not want InfiniteDrive to overwrite them.
        /// </summary>
        [DataMember]
        public bool EnableNfoHints { get; set; } = true;

        // ╔══════════════════════════════════════════════════════════════════════╗
        // ║  CACHE & RESOLUTION                                                  ║
        // ╚══════════════════════════════════════════════════════════════════════╝

        /// <summary>
        /// How many minutes a resolved stream URL remains valid before it is
        /// considered stale and re-validated at playback time.
        ///
        /// Real-Debrid / AllDebrid CDN URLs typically expire server-side at ~4–6 h
        /// after issuance.  PlaybackService adds a proactive range-probe at 70% of
        /// this TTL (≈ 252 min for the 360 min default) to catch silent URL expiry
        /// before the cache considers the entry stale.
        ///
        /// Default: 360 min (6 hours).
        /// </summary>
        [DataMember]
        public int CacheLifetimeMinutes { get; set; } = 360;

        /// <summary>
        /// Maximum number of AIOStreams API calls allowed per calendar day (UTC).
        /// AIOStreams itself may call multiple upstream addons per request.
        /// Default: 2000.
        /// </summary>
        [DataMember]
        public int ApiDailyBudget { get; set; } = 2000;

        /// <summary>
        /// Maximum number of simultaneous AIOStreams HTTP calls during background
        /// link pre-resolution.  Default: 3.
        /// </summary>
        [DataMember]
        public int MaxConcurrentResolutions { get; set; } = 3;

        /// <summary>
        /// Resolved AIOStreams instance type (Shared or Private).
        /// Auto-detected from <see cref="PrimaryManifestUrl"/> on every config save.
        /// Not user-editable — stored in XML only.
        /// Default: <see cref="Services.InstanceType.Shared"/> (safer fallback).
        /// </summary>
        [DataMember]
        public Services.InstanceType ResolvedInstanceType { get; set; } = Services.InstanceType.Shared;

        /// <summary>
        /// Maximum number of items fetched from any single catalog source per sync run.
        /// Protects API quota and SQLite performance.  Default: 500.
        /// </summary>
        [DataMember]
        public int CatalogItemCap { get; set; } = 500;

        /// <summary>
        /// Per-catalog item limit overrides, serialised as a JSON object mapping
        /// source keys to integer limits.
        ///
        /// Format: <c>{"aio:movie:30ae3b0.tmdb.top":200,"aio:series:668e3b0.nfx":50}</c>
        ///
        /// Leave empty to use <see cref="CatalogItemCap"/> for every catalog.
        /// The config page builds and saves this JSON automatically from the
        /// per-row limit inputs on the Catalog panel.
        /// </summary>
        [DataMember]
        public string CatalogItemLimitsJson { get; set; } = string.Empty;

        /// <summary>
        /// How many hours must pass since a catalog source's last <em>successful</em>
        /// sync before it is eligible to be re-fetched.
        ///
        /// The scheduled task may still run on its own Emby schedule; this setting
        /// acts as an additional internal throttle so catalog endpoints are not
        /// hammered on every task invocation.
        ///
        /// Sources in an <c>error</c> state are always retried regardless of this value.
        /// Default: 24 h.
        /// </summary>
        [DataMember]
        public int CatalogSyncIntervalHours { get; set; } = 24;

        // ╔══════════════════════════════════════════════════════════════════════╗
        // ║  STREAMING / PROXY                                                   ║
        // ╚══════════════════════════════════════════════════════════════════════╝

        /// <summary>
        /// Controls how streams are served to Emby clients.
        /// <list type="bullet">
        ///   <item><c>auto</c> — redirect first, learn per-client (recommended)</item>
        ///   <item><c>redirect</c> — always HTTP 302 to the debrid/CDN URL</item>
        ///   <item><c>proxy</c> — always passthrough proxy (needed for Samsung/LG TVs)</item>
        /// </list>
        /// </summary>
        [DataMember]
        public string ProxyMode { get; set; } = "auto";

        /// <summary>
        /// Maximum number of simultaneously proxied streams before new requests fall
        /// back to redirect mode.  Each proxy stream uses ~256 KB RAM.  Default: 5.
        /// </summary>
        [DataMember]
        public int MaxConcurrentProxyStreams { get; set; } = 5;

        // ╔══════════════════════════════════════════════════════════════════════╗
        // ║  STREAM SIGNING SECRET                                               ║
        // ╚══════════════════════════════════════════════════════════════════════╝

        /// <summary>
        /// HMAC-SHA256 signing secret used to sign .strm file URLs.
        ///
        /// Auto-generated as 32 random bytes (base64) on first plugin load.
        /// Stored here so it survives plugin reloads and Emby restarts.
        ///
        /// WARNING: Rotating this secret invalidates ALL existing .strm files.
        /// After rotation, trigger a full catalog sync to regenerate them.
        ///
        /// Do not share this value — it is equivalent to a server-side API key.
        /// </summary>
        [DataMember]
        public string PluginSecret { get; set; } = string.Empty;

        // ╔══════════════════════════════════════════════════════════════════════╗
        // ║  MULTI-PROVIDER PRIORITY                                             ║
        // ╚══════════════════════════════════════════════════════════════════════╝

        /// <summary>
        /// Comma-separated provider priority order.  Within the same quality tier,
        /// InfiniteDrive picks the stream whose provider appears earliest in this list.
        ///
        /// Uses the <c>service.id</c> values returned by AIOStreams:
        /// <c>realdebrid</c>, <c>torbox</c>, <c>alldebrid</c>, <c>premiumize</c>,
        /// <c>debridlink</c>, <c>stremthru</c>, <c>easynews</c>, <c>nzbdav</c>,
        /// <c>altmount</c>, <c>usenet</c>, <c>http</c>.
        ///
        /// Providers not listed are ranked after all listed ones.
        /// Quality tier always takes precedence over provider priority:
        /// a 4K RD stream beats a 1080p TorBox stream regardless of this setting.
        ///
        /// Default: <c>realdebrid,torbox,alldebrid,premiumize,stremthru,usenet,http</c>
        /// </summary>
        [DataMember]
        public string ProviderPriorityOrder { get; set; }
            = "realdebrid,torbox,alldebrid,debridlink,premiumize,stremthru,usenet,http";

        /// <summary>
        /// Maximum number of ranked stream candidates to store <b>per debrid provider</b>
        /// per catalog item.
        ///
        /// With <c>CandidatesPerProvider = 3</c> and three providers (RD, TorBox, Premiumize),
        /// the plugin stores up to 9 candidates — 3 from each.  PlaybackService tries them
        /// in quality order: if all 3 RD CDN URLs have expired, it automatically falls over
        /// to TorBox rank-0 before calling AIOStreams again.  This costs no extra API calls —
        /// AIOStreams already returns 80+ streams per request; we simply keep more of them.
        ///
        /// Default: 3.  Raise to 5 for extra resilience; lower to 1 to save DB space.
        /// </summary>
        [DataMember]
        public int CandidatesPerProvider { get; set; } = 3;

        /// <summary>
        /// Timeout in seconds for on-demand (synchronous) AIOStreams resolution
        /// triggered by a cache miss during playback.
        ///
        /// Acts as a minimum floor: if the AIOStreams manifest advertises a longer
        /// <c>behaviorHints.requestTimeout</c>, that value is used instead (stored
        /// in <see cref="AioStreamsDiscoveredTimeoutSeconds"/> automatically).
        ///
        /// Keep high enough for AIOStreams to query all its configured addons
        /// (typically 20–60 s depending on addon count).  Default: 30 s.
        /// </summary>
        [DataMember]
        public int SyncResolveTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// The <c>behaviorHints.requestTimeout</c> value read from the AIOStreams
        /// manifest during the last successful catalog sync.  Updated automatically —
        /// do not set this manually.  A value of 0 means not yet discovered.
        ///
        /// PlaybackService uses <c>Max(SyncResolveTimeoutSeconds, AioStreamsDiscoveredTimeoutSeconds)</c>
        /// so the manifest value acts as a ceiling that grows automatically when the
        /// user adds slow addons to their AIOStreams instance.
        /// </summary>
        [DataMember]
        public int AioStreamsDiscoveredTimeoutSeconds { get; set; } = 0;

        /// <summary>
        /// Display name of the connected AIOStreams instance, read from
        /// <c>manifest.name</c> during the last successful sync.
        /// </summary>
        [DataMember]
        public string AioStreamsDiscoveredName { get; set; } = string.Empty;

        /// <summary>
        /// Version string of the connected AIOStreams instance (<c>manifest.version</c>).
        /// </summary>
        [DataMember]
        public string AioStreamsDiscoveredVersion { get; set; } = string.Empty;

        /// <summary>
        /// True when the connected AIOStreams instance has no catalog entries
        /// (stream-only mode).  The Emby library must be populated via Trakt or
        /// MDBList; AIOStreams is used only for on-demand stream resolution.
        /// </summary>
        [DataMember]
        public bool AioStreamsIsStreamOnly { get; set; } = false;

        /// <summary>
        /// Comma-separated ID prefixes the stream resource accepts
        /// (e.g. <c>tt,imdb,mal:,kitsu:</c>).  Populated during sync.
        /// InfiniteDrive currently generates only <c>tt</c> (IMDB) IDs.
        /// </summary>
        [DataMember]
        public string AioStreamsStreamIdPrefixes { get; set; } = string.Empty;

        /// <summary>
        /// When <c>true</c> (default), InfiniteDrive automatically adds Cinemeta
        /// (<c>https://v3-cinemeta.strem.io</c>) as a catalog source when the
        /// primary AIOStreams instance has no available catalogs.
        ///
        /// This ensures new users always have Top Movies and Top Series in their
        /// Emby library even before configuring AIOStreams with catalog addons.
        ///
        /// Cinemeta is auto-injected only when:
        /// <list type="bullet">
        ///   <item>AIOStreams is not configured, or is known to be stream-only
        ///         (no catalog entries in its manifest).</item>
        /// </list>
        ///
        /// Disable this only if you intentionally have no catalog source and do
        /// not want Cinemeta items in your library.
        /// </summary>
        [DataMember]
        public bool EnableCinemetaDefault { get; set; } = true;

        // ╔══════════════════════════════════════════════════════════════════════╗
        // ║  VERSIONED PLAYBACK                                                  ║
        // ╚══════════════════════════════════════════════════════════════════════╝

        /// <summary>
        /// How many hours a normalized stream candidate remains valid before
        /// it is considered expired and cleaned up.  The <c>expires_at</c> column
        /// in the <c>candidates</c> table is computed as
        /// <c>datetime('now', '+' || CandidateTtlHours || ' hours')</c>.
        ///
        /// Expired candidates are pruned by <c>DeleteExpiredCandidatesAsync()</c>.
        /// Default: 6 hours.
        /// </summary>
        [DataMember]
        public int CandidateTtlHours { get; set; } = 6;

        /// <summary>
        /// Slot key of the default quality version used for playback when no
        /// specific slot is requested.  Must match a row in the
        /// <c>version_slots</c> table where <c>is_enabled = 1</c>.
        ///
        /// Default: <c>hd_broad</c> (1080p SDR Broad).
        /// </summary>
        [DataMember]
        public string DefaultSlotKey { get; set; } = "hd_broad";

        /// <summary>
        /// Last known Emby server LAN address, stored as "host:port" (normalized).
        /// Compared against the current address on every server startup by
        /// <see cref="Services.VersionPlaybackStartupDetector"/>.  When a change
        /// is detected, all materialized .strm files are rewritten with the new URL.
        ///
        /// Empty on fresh install — populated automatically on first startup.
        /// </summary>
        [DataMember]
        public string LastKnownServerAddress { get; set; } = string.Empty;

        /// <summary>
        /// Queue of pending rehydration operations serialised as JSON.
        ///
        /// Each entry is an object with a <c>type</c> field and a <c>slotKey</c> field:
        /// <c>[{"type":"AddSlot","slotKey":"4k_hdr"}, ...]</c>
        ///
        /// Consumed by the <c>RehydrationTask</c> on next execution to add, remove,
        /// or rename .strm/.nfo file pairs across the catalog.
        /// </summary>
        [DataMember]
        public List<string> PendingRehydrationOperations { get; set; } = new();

        // ╔══════════════════════════════════════════════════════════════════════╗
        // ║  NEXT-UP PRE-WARM                                                    ║
        // ╚══════════════════════════════════════════════════════════════════════╝

        /// <summary>
        /// Number of subsequent episodes to queue for Tier 1 pre-resolution when
        /// playback of an episode stops.
        ///
        /// Example: value of <c>2</c> queues episode+1 and episode+2 so both are
        /// ready to play instantly in sequence without a cache-miss delay.
        /// Season boundaries are crossed automatically.
        ///
        /// Higher values use more API budget.  Set to <c>0</c> to disable next-up
        /// pre-warming entirely.  Default: 2.
        /// </summary>
        [DataMember]
        public int NextUpLookaheadEpisodes { get; set; } = 2;

        // ╔══════════════════════════════════════════════════════════════════════╗
        // ║  SYNC SCHEDULE                                                       ║
        // ╚══════════════════════════════════════════════════════════════════════╝

        /// <summary>
        /// Hour of day (0–23 UTC) at which the daily catalog sync trigger fires.
        /// Default: 3 (3:00 AM).  Set to -1 to disable the daily trigger and
        /// rely solely on the Emby Scheduled Tasks page to control timing.
        /// </summary>
        [DataMember]
        public int SyncScheduleHour { get; set; } = 3;


        // ╔══════════════════════════════════════════════════════════════════════╗
        // ║  LIBRARY RE-ADOPTION                                                 ║
        // ╚══════════════════════════════════════════════════════════════════════╝

        /// <summary>
        /// When <c>true</c> (default), .strm files are deleted when real media files
        /// are detected for the same item.
        /// </summary>
        [DataMember]
        public bool DeleteStrmOnReadoption { get; set; } = true;

        // ╔══════════════════════════════════════════════════════════════════════╗
        // ║  METADATA ENRICHMENT                                                  ║
        // ╚══════════════════════════════════════════════════════════════════════╝

        /// <summary>
        /// Base URL for AIOMetadata API for enrichment.
        /// Format: https://<instance>/meta/{type}/{id}.json
        /// </summary>
        [DataMember]
        public string AioMetadataBaseUrl { get; set; } = string.Empty;

        // ╔══════════════════════════════════════════════════════════════════════╗
        // ║  FIRST-RUN WIZARD                                                    ║
        // ╚══════════════════════════════════════════════════════════════════════╝

        /// <summary>
        /// Set to <c>true</c> by the first-run wizard after initial configuration
        /// is complete.  While <c>false</c>, the dashboard shows the setup wizard.
        /// </summary>
        [DataMember]
        public bool IsFirstRunComplete { get; set; } = false;

        // ╔══════════════════════════════════════════════════════════════════════╗
        // ║  INSTANCE TYPE DETECTION                                             ║
        // ╚══════════════════════════════════════════════════════════════════════╝

        /// <summary>
        /// Known public/shared AIOStreams instance hostnames.
        /// Add new shared-hosting domains here as they emerge.
        /// </summary>
        private static readonly string[] SharedInstanceHosts =
        {
            "elfhosted.com",
            "aiostreams.elfhosted.com",
        };

        /// <summary>
        /// Detects the AIOStreams instance type from the manifest URL.
        /// <list type="bullet">
        ///   <item><c>Private</c> if host is localhost, 127.0.0.1, or RFC1918 range.</item>
        ///   <item><c>Shared</c> if host matches known public-instance allowlist.</item>
        ///   <item><c>Shared</c> for everything else (safer default).</item>
        /// </list>
        /// </summary>
        public static Services.InstanceType DetectInstanceType(string manifestUrl)
        {
            if (string.IsNullOrWhiteSpace(manifestUrl))
                return Services.InstanceType.Shared;

            try
            {
                var uri = new Uri(manifestUrl);
                var host = uri.Host.ToLowerInvariant();

                // Loopback
                if (host == "localhost" || host == "127.0.0.1" || host == "::1")
                    return Services.InstanceType.Private;

                // RFC1918 private ranges
                if (host.StartsWith("10.") ||
                    host.StartsWith("192.168.") ||
                    Is17216To31(host))
                    return Services.InstanceType.Private;

                // Known shared instances (explicit allowlist)
                foreach (var shared in SharedInstanceHosts)
                {
                    if (host == shared || host.EndsWith("." + shared))
                        return Services.InstanceType.Shared;
                }
            }
            catch
            {
                // Unparseable URL — assume shared (safer)
            }

            return Services.InstanceType.Shared;
        }

        private static bool Is17216To31(string host)
        {
            // 172.16.0.0 – 172.31.255.255
            if (!host.StartsWith("172."))
                return false;
            var parts = host.Split('.');
            if (parts.Length < 2) return false;
            if (!int.TryParse(parts[1], out var second)) return false;
            return second >= 16 && second <= 31;
        }

        // ╔══════════════════════════════════════════════════════════════════════╗
        // ║  BOUNDS VALIDATION                                                   ║
        // ╚══════════════════════════════════════════════════════════════════════╝

        /// <summary>
        /// Clamps all numeric configuration fields to safe ranges after deserialisation.
        /// Prevents zero / negative values from silently corrupting behaviour.
        /// </summary>
        public void Validate()
        {
            static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

            CacheLifetimeMinutes      = Clamp(CacheLifetimeMinutes,      30,    1_440);  // 30 min – 24 h
            ApiDailyBudget            = Clamp(ApiDailyBudget,            1,     100_000);
            MaxConcurrentResolutions  = Clamp(MaxConcurrentResolutions,  1,     20);
            CatalogItemCap            = Clamp(CatalogItemCap,            1,     50_000);
            CatalogSyncIntervalHours  = Clamp(CatalogSyncIntervalHours,  1,     168);    // 1 h – 7 days
            MaxConcurrentProxyStreams  = Clamp(MaxConcurrentProxyStreams, 1,     20);
            SyncResolveTimeoutSeconds = Clamp(SyncResolveTimeoutSeconds, 5,     300);
            NextUpLookaheadEpisodes   = Clamp(NextUpLookaheadEpisodes,   0,     10);
            // -1 is the "disabled" sentinel; any other out-of-range value clamps to 0–23
            SyncScheduleHour          = SyncScheduleHour == -1 ? -1 : Clamp(SyncScheduleHour, 0, 23);
            CandidatesPerProvider     = Clamp(CandidatesPerProvider,     1,     10);
            CandidateTtlHours         = Clamp(CandidateTtlHours,         1,     168);    // 1 h – 7 days
            SignatureValidityDays    = Clamp(SignatureValidityDays,    1,     3650);
            SkipFutureEpisodes          = SkipFutureEpisodes;
            FutureEpisodeBufferDays    = Clamp(FutureEpisodeBufferDays, 0, 30);

            // Recompute instance type from manifest URL
            ResolvedInstanceType = DetectInstanceType(PrimaryManifestUrl);
        }

        [OnDeserialized]
        private void OnDeserialized(StreamingContext _)
        {
            Validate();
        }
    }

}
