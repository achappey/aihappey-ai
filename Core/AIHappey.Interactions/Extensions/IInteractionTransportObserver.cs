namespace AIHappey.Interactions.Extensions;

/// <summary>Observes native transport content without adding mapped response events.</summary>
public interface IInteractionTransportObserver
{
    ValueTask OnBodyAsync(string kind, string content, string mediaType, CancellationToken cancellationToken);
    Stream ObserveStream(Stream stream, string mediaType);
}
