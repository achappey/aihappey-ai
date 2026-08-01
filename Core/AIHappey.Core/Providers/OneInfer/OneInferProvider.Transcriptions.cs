using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.OneInfer;

public partial class OneInferProvider
{


    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOpenAITranscriptionRequest();
        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await TranscriptionRequest(request, cancellationToken);
        return response.ToOpenAITranscriptionResponse(responseFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOpenAITranscriptionRequest();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await TranscriptionRequest(request, cancellationToken);
        if (!string.IsNullOrWhiteSpace(response.Text))
        {
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
            yield return new OpenAITranscriptionTextDone { Text = response.Text };
        }
    }

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        await ApplyAuthHeaderAsync(cancellationToken);

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var audioString = request.Audio switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        var audioBytes = Convert.FromBase64String(audioString.RemoveDataUrlPrefix());
        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = GetOneInferProviderOptions(request.ProviderOptions);
        var requestFields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["model"] = request.Model,
            ["file"] = new
            {
                fileName,
                mediaType = request.MediaType,
                bytes = audioBytes.LongLength
            }
        };

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audioBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(request.Model, Encoding.UTF8), "model");
        AddOneInferMultipartMetadata(form, metadata, requestFields, "file", "model");

        using var response = await _client.PostAsync("v1/ula/generate-audio", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OneInfer transcription failed ({(int)response.StatusCode}): {raw}");

        if (!TryParseOneInferJson(raw, out var document))
        {
            return new TranscriptionResponse
            {
                Text = raw,
                Warnings = warnings,
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
                Response = new ResponseData
                {
                    Timestamp = now,
                    Headers = response.GetHeaders(),
                    ModelId = request.Model.ToModelId(GetIdentifier()),
                    Body = raw
                },
                Request = new TranscriptionRequestItem
                {
                    Body = JsonSerializer.Serialize(requestFields, OneInferJsonOptions)
                }
            };
        }

        using (document)
        {
            var root = document.RootElement.Clone();
            var data = OneInferGetData(root);
            var text = OneInferTryGetString(data, "text", "transcript") ?? string.Empty;
            var language = OneInferTryGetString(data, "language") ?? OneInferTryGetString(metadata, "language");

            return new TranscriptionResponse
            {
                Text = text,
                Language = language,
                DurationInSeconds = OneInferTryGetFloat(data, "duration", "durationInSeconds", "duration_seconds"),
                Segments = ParseOneInferTranscriptionSegments(data),
                Warnings = warnings,
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
                Response = new ResponseData
                {
                    Timestamp = ReadOneInferUnixTimestamp(data, "created") ?? now,
                    Headers = response.GetHeaders(),
                    ModelId = request.Model.ToModelId(GetIdentifier()),
                    Body = root
                },
                Request = new TranscriptionRequestItem
                {
                    Body = JsonSerializer.Serialize(requestFields, OneInferJsonOptions)
                }
            };
        }
    }

}
