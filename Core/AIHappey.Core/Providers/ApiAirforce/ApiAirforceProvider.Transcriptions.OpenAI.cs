using System.Runtime.CompilerServices;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.ApiAirforce;

public partial class ApiAirforceProvider
{

   
    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        await using var input = options.File.OpenReadStream();
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory, cancellationToken);

        var fields = new Dictionary<string, object?>
        {
            ["language_code"] = options.Language,
            ["timestamps_granularity"] = options.TimestampGranularities?.FirstOrDefault()
        };

        var result = await SendApiAirforceTranscriptionAsync(
            NormalizeModelId(options.Model),
            memory.ToArray(),
            string.IsNullOrWhiteSpace(options.File.ContentType) ? "audio/mpeg" : options.File.ContentType,
            fields,
            cancellationToken);

        var response = new Vercel.Models.TranscriptionResponse
        {
            Text = TryGetString(result.Root, "text") ?? string.Empty,
            Language = TryGetString(result.Root, "language_code"),
            DurationInSeconds = result.Root.TryGetProperty("audio_duration_secs", out var duration) && duration.TryGetSingle(out var seconds) ? seconds : null,
            Segments = result.Root.TryGetProperty("words", out var words) && words.ValueKind == System.Text.Json.JsonValueKind.Array
                ? words.EnumerateArray()
                    .Where(word => string.Equals(TryGetString(word, "type"), "word", StringComparison.OrdinalIgnoreCase))
                    .Select(word => new Vercel.Models.TranscriptionSegment
                    {
                        Text = TryGetString(word, "text") ?? string.Empty,
                        StartSecond = TryGetSingle(word, "start"),
                        EndSecond = TryGetSingle(word, "end")
                    }).ToArray()
                : []
        };

        return response.ToOpenAITranscriptionResponse(options.ResolveOpenAITranscriptionResponseFormat());
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
