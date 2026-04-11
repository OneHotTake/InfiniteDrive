namespace InfiniteDrive.Models
{
    /// <summary>
    /// Stream endpoint request model.
    /// </summary>
    public class StreamEndpointRequest
    {
        /// <summary>
        /// Short-lived stream token for authenticating requests.
        /// Format: {quality}:{imdbId}:{exp}:{signature}
        /// </summary>
        public string? Token { get; set; }

        /// <summary>
        /// Optional upstream URL to proxy directly without token validation.
        /// Used for CDN redirection or testing.
        /// </summary>
        public string? Url { get; set; }
    }
}
