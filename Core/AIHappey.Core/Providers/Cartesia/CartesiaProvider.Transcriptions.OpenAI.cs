using System.Text.Json;
using System.Runtime.CompilerServices;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Cartesia;

public partial class CartesiaProvider
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
        request.ProviderOptions = CreateCartesiaOpenAITranscriptionOptions(options);
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

    private Dictionary<string, JsonElement>? CreateCartesiaOpenAITranscriptionOptions(
        OpenAITranscriptionRequest options)
    {
        var metadata = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(options.Language))
            metadata["language"] = options.Language;

        if (options.TimestampGranularities?.Any() == true)
            metadata["timestampGranularities"] = options.TimestampGranularities;

        return metadata.Count == 0
            ? null
            : new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web)
            };
    }
}

