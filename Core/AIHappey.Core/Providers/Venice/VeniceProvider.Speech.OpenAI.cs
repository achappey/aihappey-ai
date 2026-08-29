using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Venice;

public partial class VeniceProvider
{
    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ValidateVeniceOpenAISpeechRequest(options);
        var request = CreateVeniceSpeechRequest(options, streaming: false);
        var response = await SpeechRequest(request, cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateVeniceOpenAISpeechRequest(options);
        ApplyAuthHeader();

        var request = CreateVeniceSpeechRequest(options, streaming: true);
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = metadata.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(metadata.GetRawText()) as JsonObject ?? []
            : [];

        SetIfMissing(payload, "input", request.Text);
        SetIfMissing(payload, "model", request.Model);
        SetIfMissing(payload, "voice", request.Voice);
        if (request.Speed is not null)
            SetIfMissing(payload, "speed", request.Speed.Value);
        SetIfMissing(payload, "response_format", NormalizeSpeechFormat(request.OutputFormat));
        payload["streaming"] = true;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(payload.ToJsonString(JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var rawError = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Venice speech request failed ({(int)response.StatusCode}): {rawError}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            yield return new AudioSpeechStreamDelta
            {
                Audio = Convert.ToBase64String(buffer, 0, read)
            };
        }

        yield return new AudioSpeechStreamDone();
    }

    private static void ValidateVeniceOpenAISpeechRequest(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("'model' is a required field");
        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("'input' is a required field");
    }

    private SpeechRequest CreateVeniceSpeechRequest(AudioSpeechRequest options, bool streaming)
    {
        var request = options.ToSpeechRequest();
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in options.AdditionalProperties ?? [])
            payload[name] = JsonSerializer.Deserialize<object?>(value.GetRawText(), JsonSerializerOptions.Web);

        // Venice's style-control equivalent of OpenAI instructions is `prompt`.
        if (!string.IsNullOrWhiteSpace(options.Instructions))
            payload["prompt"] = options.Instructions;
        payload["streaming"] = streaming;

        request.Instructions = null;
        request.ProviderOptions = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web)
        };
        return request;
    }
}
