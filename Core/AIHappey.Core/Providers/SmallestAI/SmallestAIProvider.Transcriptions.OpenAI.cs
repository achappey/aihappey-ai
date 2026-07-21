using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Model.Providers.SmallestAI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SmallestAI;

public partial class SmallestAIProvider
{
    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(
            options.Model,
            GetIdentifier(),
            cancellationToken);
        request.ProviderOptions = CreateSmallestAiOpenAITranscriptionOptions(options, request);

        var response = await TranscriptionRequest(request, cancellationToken);
        return response.ToOpenAITranscriptionResponse(responseFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);

        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };

        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private Dictionary<string, JsonElement>? CreateSmallestAiOpenAITranscriptionOptions(
        OpenAITranscriptionRequest options,
        TranscriptionRequest request)
    {
        var existing = request.GetProviderMetadata<SmallestAITranscriptionProviderMetadata>(GetIdentifier());
        var wordTimestampsRequested = options.TimestampGranularities?.Any(granularity =>
            string.Equals(granularity?.Trim(), "word", StringComparison.OrdinalIgnoreCase)) == true;

        var metadata = new SmallestAITranscriptionProviderMetadata
        {
            Language = !string.IsNullOrWhiteSpace(options.Language)
                ? options.Language.Trim()
                : existing?.Language,
            WordTimestamps = wordTimestampsRequested ? true : existing?.WordTimestamps,
            Diarize = existing?.Diarize,
            GenderDetection = existing?.GenderDetection,
            EmotionDetection = existing?.EmotionDetection,
            WebhookUrl = existing?.WebhookUrl,
            WebhookMethod = existing?.WebhookMethod,
            WebhookExtra = existing?.WebhookExtra,
            RedactPii = existing?.RedactPii,
            RedactPci = existing?.RedactPci
        };

        return metadata.Language is not null
            || metadata.WordTimestamps is not null
            || metadata.Diarize is not null
            || metadata.GenderDetection is not null
            || metadata.EmotionDetection is not null
            || metadata.WebhookUrl is not null
            || metadata.WebhookMethod is not null
            || metadata.WebhookExtra is not null
            || metadata.RedactPii is not null
            || metadata.RedactPci is not null
            ? new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web)
            }
            : null;
    }
}

