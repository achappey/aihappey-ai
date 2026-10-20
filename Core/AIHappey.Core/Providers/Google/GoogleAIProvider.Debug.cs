using AIHappey.Core.Diagnostics;
using AIHappey.Interactions.Extensions;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    private GoogleInteractionDebugCapture? CreateGoogleInteractionDebugCapture(string operation)
        => _debug.Enabled ? new(_debug, GetIdentifier(), operation) : null;

    private sealed class GoogleInteractionDebugCapture(IProviderDebugEmitter emitter, string provider, string operation)
        : IInteractionTransportObserver
    {
        private readonly string operationId = Guid.NewGuid().ToString("n");
        public bool Failed { get; private set; }

        public async ValueTask OnBodyAsync(string kind, string content, string mediaType, CancellationToken cancellationToken)
        {
            try
            {
                await emitter.EmitAsync(provider, operation, operationId, kind,
                    ProviderDebugPayload.FromText(content, mediaType), cancellationToken);
            }
            catch
            {
                Failed = true;
                throw;
            }
        }

        public Stream ObserveStream(Stream stream, string mediaType)
            => new DebugResponseStream(stream, emitter, provider, operation, operationId, mediaType);
    }
}
