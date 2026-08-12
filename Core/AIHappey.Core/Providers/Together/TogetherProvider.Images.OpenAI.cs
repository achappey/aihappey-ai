using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AIHappey.Core.Providers.Together;

public partial class TogetherProvider
{


    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageGenerationRequestAsync(
            CreateTogetherImageGenerationRequest(options),
            cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        await foreach (var streamEvent in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(
            CreateTogetherImageGenerationRequest(options),
            cancellationToken: cancellationToken))
        {
            yield return streamEvent;
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Together does not document OpenAI-compatible image edit support.");
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Together does not document OpenAI-compatible image edit streaming support.");
    }

    private static OpenAIImageGenerationRequest CreateTogetherImageGenerationRequest(OpenAIImageGenerationRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var additional = options.AdditionalProperties is null
            ? new Dictionary<string, JsonElement>()
            : new Dictionary<string, JsonElement>(options.AdditionalProperties);

        if (!string.IsNullOrWhiteSpace(options.Size))
        {
            var parts = options.Size.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height))
            {
                additional["width"] = JsonSerializer.SerializeToElement(width);
                additional["height"] = JsonSerializer.SerializeToElement(height);
            }
        }

        return new OpenAIImageGenerationRequest
        {
            Prompt = options.Prompt,
            Model = options.Model,
            N = options.N,
            OutputFormat = options.OutputFormat,
            ResponseFormat = "base64",
            AdditionalProperties = additional.Count == 0 ? null : additional
        };
    }
}
