using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Venice;

public partial class VeniceProvider
{

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        var requestedFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in options.AdditionalProperties ?? [])
            payload[name] = JsonSerializer.Deserialize<object?>(value.GetRawText(), JsonSerializerOptions.Web);

        if (!string.IsNullOrWhiteSpace(options.Language))
            payload["language"] = options.Language;

        // Venice exposes only json/text. verbose_json is synthesized from its timestamp response.
        payload["response_format"] = requestedFormat == "text" ? "text" : "json";
        if (options.TimestampGranularities?.Length > 0 || requestedFormat == "verbose_json")
            payload["timestamps"] = true;

        request.ProviderOptions = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web)
        };

        var response = await TranscriptionRequest(request, cancellationToken);
        return response.ToOpenAITranscriptionResponse(requestedFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Venice transcription is synchronous. Mimic OpenAI streaming with a delta and done event.
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

}
