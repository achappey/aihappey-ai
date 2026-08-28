using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.ResembleAI;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.ResembleAI;

public partial class ResembleAIProvider
{

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();

        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(
            options.Model,
            GetIdentifier(),
            cancellationToken);

        if (options.AdditionalProperties is { Count: > 0 })
        {
            var passthrough = request.ProviderOptions is not null
                && request.ProviderOptions.TryGetValue(GetIdentifier(), out var generated)
                && generated.ValueKind == JsonValueKind.Object
                    ? generated.EnumerateObject().ToDictionary(
                        property => property.Name,
                        property => property.Value.Clone(),
                        StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in options.AdditionalProperties)
                passthrough[property.Key] = property.Value.Clone();

            request.ProviderOptions = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(passthrough)
            };
        }

        var response = await TranscriptionRequest(request, cancellationToken);
        return response.ToOpenAITranscriptionResponse(responseFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };

        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }
}

