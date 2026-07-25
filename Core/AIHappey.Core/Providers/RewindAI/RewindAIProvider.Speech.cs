using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.RewindAI;

public partial class RewindAIProvider
{

     public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
     {
         ArgumentNullException.ThrowIfNull(request);
         if (string.IsNullOrWhiteSpace(request.Text))
             throw new ArgumentException("Text is required.", nameof(request));

         ApplyAuthHeader();
         var payload = CreateRewindAIPayload(request.ProviderOptions,
             ("text", request.Text),
             ("voice", request.Voice),
             ("speed", request.Speed),
             ("format", request.OutputFormat));
         var requestBody = JsonSerializer.Serialize(payload, RewindAIJson);
         using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/tts/")
         {
             Content = new StringContent(requestBody, Encoding.UTF8, MediaTypeNames.Application.Json)
         };
         using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
         var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
         if (!response.IsSuccessStatusCode)
             throw new InvalidOperationException($"RewindAI speech generation failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

         var contentType = response.Content.Headers.ContentType?.MediaType;
         var format = string.IsNullOrWhiteSpace(request.OutputFormat) ? "mp3" : request.OutputFormat.Trim();
         return new SpeechResponse
         {
             Audio = new SpeechAudioResponse
             {
                 Base64 = Convert.ToBase64String(bytes),
                 MimeType = contentType ?? GetRewindAISpeechMimeType(format),
                 Format = format
             },
             ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
             {
                 contentType,
                 contentLength = bytes.LongLength
             }),
             Request = new SpeechRequestItem { Body = payload },
             Response = new ResponseData
             {
                 Timestamp = DateTime.UtcNow,
                 Headers = response.GetHeaders(),
                 ModelId = request.Model.ToModelId(GetIdentifier()),
                 Body = new { contentType, contentLength = bytes.LongLength }
             }
         };
     }
        
    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        foreach (var streamEvent in response.ToOpenAISpeechStreamEvents())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

}
