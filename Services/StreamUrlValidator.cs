using System;

namespace EmbyStreams.Services
{
    /// <summary>
    /// Validates stream URLs before caching or serving.
    /// Ensures URLs are absolute, non-empty, and well-formed.
    /// </summary>
    public static class StreamUrlValidator
    {
        /// <summary>
        /// Returns false if the URL is invalid and should trigger re-resolution.
        /// Invalid conditions:
        /// <list type="bullet">
        ///   <item>url is null or whitespace</item>
        ///   <item>url is not an absolute URI</item>
        ///   <item>URI path is "/" or empty</item>
        /// </list>
        /// </summary>
        /// <param name="url">The stream URL to validate.</param>
        /// <returns>True if valid, false if invalid.</returns>
        public static bool IsValid(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            var path = uri.AbsolutePath;

            // Reject root path or empty path
            if (string.IsNullOrEmpty(path) || path == "/")
                return false;

            return true;
        }
    }
}
