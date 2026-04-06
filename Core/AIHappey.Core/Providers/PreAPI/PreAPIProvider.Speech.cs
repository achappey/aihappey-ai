using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.PreAPI;

public partial class PreAPIProvider
{
   public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        var input = BuildSpeechInput(request);
        using var doc = await GenerateAsync(request.Model, input, cancellationToken);

        var data = GetResponseData(doc.RootElement);
        var output = GetOutput(data);
        var audioUrl = TryGetNestedString(output, "audio", "url") ?? TryGetString(data, "output_url");
        var contentType = TryGetNestedString(output, "audio", "content_type");

        if (string.IsNullOrWhiteSpace(audioUrl))
            throw new InvalidOperationException("PreAPI speech generation returned no audio URL.");

        var download = await DownloadMediaAsync(audioUrl, contentType, cancellationToken);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = download.Base64,
                MimeType = download.MediaType,
                Format = GuessAudioFormat(download.MediaType, audioUrl)
            },
            Warnings = warnings,
            ProviderMetadata = CreateProviderMetadata(doc.RootElement),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = doc.RootElement.Clone()
            }
        };
    }

}
