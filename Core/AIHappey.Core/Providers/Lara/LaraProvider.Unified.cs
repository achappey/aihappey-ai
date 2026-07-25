using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using AIHappey.Common.Model.Providers.Lara;
using System.Runtime.CompilerServices;
using Lara.Sdk;

namespace AIHappey.Core.Providers.Lara;

public partial class LaraProvider
{
  public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    var modelId = request.Model ?? throw new ArgumentException("Model is required.", nameof(request));
    var targetLanguage = GetTargetLanguage(modelId);
    var metadata = request.Metadata.GetProviderMetadata<LaraProviderMetadata>(GetIdentifier());
    var sourceText = GetLatestUserText(request);
    var result = await CreateTranslator().Translate<string>(
        sourceText,
        metadata?.Source,
        targetLanguage,
        CreateTranslateOptions(metadata));

    var text = result.Translation ?? string.Empty;

    return new AIResponse
    {
      ProviderId = GetIdentifier(),
      Model = modelId,
      Status = "completed",
      Usage = new Dictionary<string, object?>(),
      Metadata = new Dictionary<string, object?>
      {
        ["finishReason"] = "stop",
        ["targetLanguage"] = targetLanguage,
        ["sourceLanguage"] = result.SourceLanguage
      },
      Output = new AIOutput
      {
        Items =
            [
                new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content = [new AITextContentPart { Text = text, Type = "text" }]
                    }
            ]
      }
    };
  }

  public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
      AIRequest request,
      [EnumeratorCancellation] CancellationToken cancellationToken = default)
  {
    var response = await ExecuteUnifiedAsync(request, cancellationToken);
    var text = response.Output?.Items?
        .SelectMany(item => item.Content ?? [])
        .OfType<AITextContentPart>()
        .FirstOrDefault()?.Text ?? string.Empty;
    var eventId = Guid.NewGuid().ToString("n");
    var timestamp = DateTimeOffset.UtcNow;

    yield return CreateStreamEvent(eventId, "text-start", new AITextStartEventData(), timestamp, response.Metadata);
    if (!string.IsNullOrEmpty(text))
      yield return CreateStreamEvent(eventId, "text-delta", new AITextDeltaEventData { Delta = text }, timestamp, response.Metadata);
    yield return CreateStreamEvent(eventId, "text-end", new AITextEndEventData(), timestamp, response.Metadata);
    yield return CreateStreamEvent(eventId, "finish", new AIFinishEventData
    {
      FinishReason = "stop",
      Model = response.Model,
      CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
      MessageMetadata = AIFinishMessageMetadata.Create(
            response.Model ?? string.Empty,
            DateTimeOffset.UtcNow,
            response.Usage as Dictionary<string, object?>,
            temperature: request.Temperature)
    }, DateTimeOffset.UtcNow, response.Metadata);
  }

  private static string GetTargetLanguage(string model)
  {
    const string prefix = "translate/";
    var normalized = model.Trim();
    if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || normalized.Length == prefix.Length)
      throw new ArgumentException("Lara model must be formatted as 'translate/<target-language>'.", nameof(model));

    return normalized[prefix.Length..].Trim();
  }

  private static string GetLatestUserText(AIRequest request)
  {
    if (!string.IsNullOrWhiteSpace(request.Input?.Text))
      return request.Input.Text;

    var parts = request.Input?.Items?
        .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))?
        .Content?
        .OfType<AITextContentPart>()
        .Select(part => part.Text)
        .Where(text => !string.IsNullOrWhiteSpace(text))
        .ToList() ?? [];

    if (parts.Count == 0)
      throw new ArgumentException("Lara requires text in the latest user message.", nameof(request));

    return string.Join("\n", parts);
  }

  private static TranslateOptions CreateTranslateOptions(LaraProviderMetadata? metadata)
      => new()
      {
        SourceHint = metadata?.SourceHint,
        AdaptTo = metadata?.AdaptTo,
        Glossaries = metadata?.Glossaries,
        Instructions = metadata?.Instructions,
        ContentType = metadata?.ContentType,
        TimeoutInMillis = metadata?.TimeoutInMillis,
        NoTrace = metadata?.NoTrace,
        Reasoning = metadata?.Reasoning,
        Style = ParseStyle(metadata?.Style)
      };

  private static TranslationStyle? ParseStyle(string? style)
      => Enum.TryParse<TranslationStyle>(style, ignoreCase: true, out var parsed) ? parsed : null;

  private AIStreamEvent CreateStreamEvent(string eventId, string type, object data, DateTimeOffset timestamp, Dictionary<string, object?>? metadata)
      => new()
      {
        ProviderId = GetIdentifier(),
        Metadata = metadata,
        Event = new AIEventEnvelope
        {
          Type = type,
          Id = eventId,
          Timestamp = timestamp,
          Data = data,
          Metadata = metadata
        }
      };
}
