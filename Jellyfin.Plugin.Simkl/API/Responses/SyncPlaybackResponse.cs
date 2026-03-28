using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Simkl.API.Responses
{
    /// <summary>
    /// Sync playback response.
    /// </summary>
    public class SyncPlaybackResponse
    {
        /// <summary>
        /// Gets or sets error message if any.
        /// </summary>
        [JsonPropertyName("error")]
        public string? Error { get; set; }

        /// <summary>
        /// Gets or sets status.
        /// </summary>
        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }
}
