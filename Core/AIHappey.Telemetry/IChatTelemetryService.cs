using AIHappey.Common.Model;
using AIHappey.Telemetry.Models;

namespace AIHappey.Telemetry
{
    public interface IChatTelemetryService
    {
        /// <summary>
        /// Persists a chat request (from any provider) to the telemetry store.
        /// </summary>
        Task TrackChatRequestAsync(ChatRequest chatRequest,
            string userId, string username,
            int inputTokens, int totalTokens,
            string provider,
            RequestType requestType,
            DateTime started, DateTime ended, CancellationToken cancellationToken = default);
    }
}
