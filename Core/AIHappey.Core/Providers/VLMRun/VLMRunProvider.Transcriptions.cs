using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.VLMRun;

public partial class VLMRunProvider
{


    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var audio = request.Audio is JsonElement { ValueKind: JsonValueKind.String } audioElement
            ? audioElement.GetString()
            : request.Audio?.ToString();

        if (string.IsNullOrWhiteSpace(audio))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audio, out _, out var parsedBase64))
            audio = parsedBase64;

        var bytes = Convert.FromBase64String(audio);
        var file = new Microsoft.AspNetCore.Http.FormFile(
            new MemoryStream(bytes, writable: false), 0, bytes.Length, "file", "audio" + request.MediaType.GetAudioExtension())
        {
            Headers = new Microsoft.AspNetCore.Http.HeaderDictionary(),
            ContentType = request.MediaType
        };

        var options = new OpenAITranscriptionRequest
        {
            Model = NormalizeVLMRunModel(request.Model),
            File = file,
            ResponseFormat = "verbose_json"
        };

        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var providerOptions) == true
            && providerOptions.ValueKind == JsonValueKind.Object)
        {
            options.Language = GetVLMRunTranscriptionString(providerOptions, "language");
            options.Prompt = GetVLMRunTranscriptionString(providerOptions, "prompt");
            options.ResponseFormat = GetVLMRunTranscriptionString(providerOptions, "response_format", "responseFormat")
                ?? options.ResponseFormat;
            options.Temperature = GetVLMRunTranscriptionFloat(providerOptions, "temperature");
            options.TimestampGranularities = GetVLMRunTranscriptionStrings(
                providerOptions, "timestamp_granularities", "timestampGranularities");
        }

        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        var verbose = response as OpenAITranscriptionVerboseResponse;

        return new TranscriptionResponse
        {
            Text = response.Text,
            Language = verbose?.Language,
            DurationInSeconds = verbose is null ? null : (float)verbose.Duration,
            Segments = verbose?.Segments?.Select(segment => new TranscriptionSegment
            {
                Text = segment.Text,
                StartSecond = (float)segment.Start,
                EndSecond = (float)segment.End
            }) ?? [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = response
            }
        };
    }




    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        options.Model = NormalizeVLMRunModel(options.Model);
        return _client.OpenAICompatibleTranscriptionRequestAsync(
            options,
            VLMRunGatewayTranscriptionsEndpoint,
            cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The Gateway transcription endpoint is currently request/response only.
        // Adapt that response to the OpenAI stream event contract used by all
        // four unified conversation routes.
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };

        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private static string? GetVLMRunTranscriptionString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();

        return null;
    }

    private static float? GetVLMRunTranscriptionFloat(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetSingle()
            : null;

    private static string[]? GetVLMRunTranscriptionStrings(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
                return value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .ToArray();
        }

        return null;
    }

}
