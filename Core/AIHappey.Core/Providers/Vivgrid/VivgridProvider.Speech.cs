using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Vivgrid;

public partial class VivgridProvider
{
    private async Task<SpeechResponse> SpeechRequestVivgrid(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["input"] = request.Text,
            ["voice"] = request.Voice,
            ["response_format"] = request.OutputFormat,
            ["speed"] = request.Speed
        };

        MergeVivgridProviderOptions(payload, request.ProviderOptions, GetIdentifier());

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, VivgridJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"Vivgrid speech generation failed ({(int)response.StatusCode}): {error}");
        }

        var format = ResolveVivgridAudioFormat(TryGetPayloadString(payload, "response_format", "responseFormat"));

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = ResolveVivgridAudioMimeType(format, response.Content.Headers.ContentType?.MediaType),
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            },
            Request = new SpeechRequestItem
            {
                Body = payload
            }
        };
    }
}
