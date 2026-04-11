using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Abstraction over any Stremio-compatible addon manifest provider.
    ///
    /// <para>
    /// Implementations wrap a single Stremio addon instance (AIOStreams,
    /// Cinemeta, AIOMetadata, etc.) and expose the four core resource types
    /// defined by the Stremio addon protocol:
    /// <list type="bullet">
    ///   <item><c>manifest</c> — addon metadata and capabilities</item>
    ///   <item><c>catalog</c> — paginated content listings</item>
    ///   <item><c>stream</c> — playable stream URLs for a given item</item>
    ///   <item><c>meta</c> — detailed metadata for a single item</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Not every provider supports all four resource types.  Cinemeta, for
    /// example, provides catalog and meta but not streams.  Callers should
    /// check <see cref="IsConfigured"/> before use and handle null returns
    /// gracefully.
    /// </para>
    /// </summary>
    public interface IManifestProvider : IDisposable
    {
        /// <summary>
        /// Fully-qualified manifest URL for this provider instance.
        /// </summary>
        string ManifestUrl { get; }

        /// <summary>
        /// Returns <c>true</c> when the provider has a non-empty base URL
        /// and is ready to serve requests.
        /// </summary>
        bool IsConfigured { get; }

        // ── Manifest ───────────────────────────────────────────────────────────

        /// <summary>
        /// Fetches and parses the addon manifest.
        /// Returns <c>null</c> on any error (connectivity, auth, parse failure).
        /// </summary>
        Task<AioStreamsManifest?> GetManifestAsync(
            CancellationToken cancellationToken = default);

        // ── Catalogs ───────────────────────────────────────────────────────────

        /// <summary>
        /// Fetches a catalog page by type and catalog ID.
        /// </summary>
        Task<AioStreamsCatalogResponse?> GetCatalogAsync(
            string type,
            string catalogId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Fetches a catalog page with optional search, genre, and pagination.
        /// </summary>
        Task<AioStreamsCatalogResponse?> GetCatalogAsync(
            string type,
            string catalogId,
            string? searchQuery,
            string? genre,
            int? skip,
            CancellationToken cancellationToken = default);

        // ── Streams ────────────────────────────────────────────────────────────

        /// <summary>
        /// Fetches streams for a movie by IMDB (or compatible) ID.
        /// </summary>
        Task<AioStreamsStreamResponse?> GetMovieStreamsAsync(
            string imdbId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Fetches streams for a TV episode.
        /// </summary>
        Task<AioStreamsStreamResponse?> GetSeriesStreamsAsync(
            string imdbId,
            int season,
            int episode,
            CancellationToken cancellationToken = default);

        // ── Metadata ───────────────────────────────────────────────────────────

        /// <summary>
        /// Fetches metadata for a single item (poster, genres, description, etc.).
        /// Returns <c>null</c> when the provider does not support the meta resource.
        /// </summary>
        Task<JsonElement?> GetMetaAsync(
            string type,
            string id,
            CancellationToken cancellationToken = default);

        // ── Health ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Tests connectivity by fetching the manifest.
        /// Returns <c>(true, null)</c> on success or <c>(false, errorMessage)</c>.
        /// </summary>
        Task<(bool Ok, string? Error)> TestConnectionAsync(
            CancellationToken cancellationToken = default);
    }
}
