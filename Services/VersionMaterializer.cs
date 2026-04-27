using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using InfiniteDrive.Models;
using InfiniteDrive.Tasks;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Writes versioned .strm and .nfo files with slot suffixes.
    /// Default slot gets unsuffixed base filename; other slots get
    /// <c>" - {FileSuffix}"</c> appended before the extension.
    /// </summary>
    public class VersionMaterializer
    {
        private readonly ILogger _logger;

        public VersionMaterializer(ILogger logger)
        {
            _logger = logger;
        }

        // ── URL Building ────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a .strm URL with resolve token for multi-tier playback.
        /// <para>
        /// Format:
        ///   <c>{embyBase}/InfiniteDrive/resolve?token={resolve_token}&amp;quality={tier}&amp;id={id}&amp;idType={type}[&amp;season={n}&amp;episode={m}]</c>
        /// </para>
        /// <para>
        /// Token format: <c>{quality}:{imdbId}:{exp}:{signature}</c>
        /// Valid for 365 days.
        /// </para>
        /// </summary>
        public string BuildStrmUrl(
            string embyBaseUrl,
            string titleId,
            string slotKey,
            string idType,
            int? season,
            int? episode)
        {
            return BuildStrmUrlWithExpiry(embyBaseUrl, titleId, slotKey, idType, season, episode).Url;
        }

        /// <summary>
        /// Builds a .strm URL with resolve token and returns both the URL and expiry timestamp.
        /// Used for token rotation tracking (Sprint 141).
        /// </summary>
        public (string Url, long ExpiresAtUnix) BuildStrmUrlWithExpiry(
            string embyBaseUrl,
            string titleId,
            string slotKey,
            string idType,
            int? season,
            int? episode)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
                throw new InvalidOperationException("Plugin configuration not available");

            var baseUrl = embyBaseUrl.TrimEnd('/');

            // Generate resolve token (365-day validity for .strm files)
            // Token is opaque - IMDB ID and quality are passed as query parameters
            var token = PlaybackTokenService.GenerateResolveToken(config.PluginSecret, config.SignatureValidityDays * 24);

            // Calculate expiry timestamp for tracking
            var expiresAtUnix = DateTimeOffset.UtcNow.AddDays(config.SignatureValidityDays).ToUnixTimeSeconds();

            var sb = new StringBuilder();
            sb.Append(baseUrl);
            sb.Append("/InfiniteDrive/resolve?");
            sb.Append("token=").Append(Uri.EscapeDataString(token));
            sb.Append("&quality=").Append(Uri.EscapeDataString(slotKey));
            sb.Append("&id=").Append(Uri.EscapeDataString(titleId));
            sb.Append("&idType=").Append(Uri.EscapeDataString(idType));

            if (season.HasValue)
            {
                sb.Append("&season=").Append(season.Value);
            }
            if (episode.HasValue)
            {
                sb.Append("&episode=").Append(episode.Value);
            }

            return (sb.ToString(), expiresAtUnix);
        }

        // ── File Naming ─────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the suffixed filename for a versioned file.
        /// <para>
        /// Default slot: <c>{baseName}{extension}</c> (no suffix)
        /// Other slots: <c>{baseName} - {slot.FileSuffix}{extension}</c>
        /// </para>
        /// </summary>
        public string GetFileName(
            string baseName,
            VersionSlot slot,
            VersionSlot defaultSlot,
            string extension)
        {
            if (slot.SlotKey == defaultSlot.SlotKey)
                return baseName + extension;

            var suffix = slot.FileSuffix;
            if (string.IsNullOrWhiteSpace(suffix))
                return baseName + extension;

            return $"{baseName} - {suffix}{extension}";
        }

        // ── .strm Writing ───────────────────────────────────────────────────────

        /// <summary>
        /// Writes a .strm file for a specific slot.
        /// Returns the absolute path to the written file.
        /// </summary>
        public string WriteStrmFile(
            string basePath,
            string baseName,
            VersionSlot slot,
            VersionSlot defaultSlot,
            string strmUrl)
        {
            var fileName = GetFileName(baseName, slot, defaultSlot, ".strm");
            var fullPath = Path.Combine(basePath, fileName);

            // Ensure directory exists
            Directory.CreateDirectory(basePath);

            File.WriteAllText(fullPath, strmUrl, new UTF8Encoding(false));

            _logger.LogDebug(
                "[VersionMaterializer] Wrote .strm: {Path} (slot={Slot}, hash={Hash})",
                fullPath, slot.SlotKey, ComputeStrmUrlHash(strmUrl).Substring(0, 8));

            return fullPath;
        }

        // ── .nfo Writing ────────────────────────────────────────────────────────

        /// <summary>
        /// Writes a .nfo file for a specific slot with synthetic stream details
        /// derived from the top candidate's technical metadata.
        /// </summary>
        /// <param name="basePath">Directory to write into.</param>
        /// <param name="baseName">Base filename (without extension).</param>
        /// <param name="slot">The version slot.</param>
        /// <param name="defaultSlot">The default slot (for naming).</param>
        /// <param name="topCandidate">Best candidate for this slot (may be null).</param>
        /// <param name="rootElement">Root XML element name ("movie" or "episodedetails").</param>
        /// <param name="item">Catalog item for title/IDs, or null for minimal NFO.</param>
        /// <returns>Absolute path to written .nfo file.</returns>
        public string WriteNfoFile(
            string basePath,
            string baseName,
            VersionSlot slot,
            VersionSlot defaultSlot,
            Candidate? topCandidate,
            string rootElement,
            CatalogItem? item)
        {
            var fileName = GetFileName(baseName, slot, defaultSlot, ".nfo");
            var fullPath = Path.Combine(basePath, fileName);

            Directory.CreateDirectory(basePath);

            var sb = new StringBuilder();
            using (var writer = XmlWriter.Create(sb, new XmlWriterSettings
            {
                Indent = true,
                Encoding = new UTF8Encoding(false),
                OmitXmlDeclaration = true
            }))
            {
                writer.WriteStartElement(rootElement);

                // Title and IDs from catalog item
                if (item != null)
                {
                    writer.WriteElementString("title", item.Title ?? string.Empty);
                    if (!string.IsNullOrEmpty(item.ImdbId))
                    {
                        writer.WriteStartElement("uniqueid");
                        writer.WriteAttributeString("type", "imdb");
                        writer.WriteAttributeString("default", "true");
                        writer.WriteString(item.ImdbId);
                        writer.WriteEndElement();
                    }
                    if (!string.IsNullOrEmpty(item.TmdbId))
                    {
                        writer.WriteStartElement("uniqueid");
                        writer.WriteAttributeString("type", "tmdb");
                        writer.WriteString(item.TmdbId);
                        writer.WriteEndElement();
                    }
                }

                // Synthetic stream details from candidate metadata
                if (topCandidate != null)
                {
                    writer.WriteComment(" Derived stream details from InfiniteDrive candidate metadata ");
                    writer.WriteStartElement("streamdetails");
                    writer.WriteStartElement("video");

                    if (!string.IsNullOrEmpty(topCandidate.Resolution))
                        writer.WriteElementString("resolution", topCandidate.Resolution);
                    if (!string.IsNullOrEmpty(topCandidate.VideoCodec))
                        writer.WriteElementString("codec", topCandidate.VideoCodec);
                    if (!string.IsNullOrEmpty(topCandidate.HdrClass))
                        writer.WriteElementString("hdr", topCandidate.HdrClass);
                    if (topCandidate.BitrateKbps.HasValue)
                        writer.WriteElementString("bitrate", (topCandidate.BitrateKbps.Value * 1000).ToString());

                    writer.WriteEndElement(); // </video>

                    if (!string.IsNullOrEmpty(topCandidate.AudioCodec)
                    || !string.IsNullOrEmpty(topCandidate.AudioChannels))
                    {
                        writer.WriteStartElement("audio");
                        if (!string.IsNullOrEmpty(topCandidate.AudioCodec))
                            writer.WriteElementString("codec", topCandidate.AudioCodec);
                        if (!string.IsNullOrEmpty(topCandidate.AudioChannels))
                            writer.WriteElementString("channels", topCandidate.AudioChannels);
                        writer.WriteEndElement(); // </audio>
                    }

                    writer.WriteEndElement(); // </streamdetails>
                }

                writer.WriteEndElement(); // </rootElement>
            }

            File.WriteAllText(fullPath, sb.ToString(), new UTF8Encoding(false));

            _logger.LogDebug(
                "[VersionMaterializer] Wrote .nfo: {Path} (slot={Slot})",
                fullPath, slot.SlotKey);

            return fullPath;
        }

        // ── Hash ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes a SHA1 hex hash of a URL for change detection.
        /// Used to avoid unnecessary .strm rewrites when the URL has not changed.
        /// </summary>
        public static string ComputeStrmUrlHash(string url)
        {
            var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(url));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
