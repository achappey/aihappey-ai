using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Common.Model;
using System.Net.Http.Headers;

namespace AIHappey.Core.Providers.AIML;

public partial class AIMLProvider : IModelProvider
{

    public async Task<TranscriptionResponse> TranscriptionRequest(
       TranscriptionRequest request,
       CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader(); // Authorization: Bearer <AIML_API_KEY>

        var model = $"{request.Model}";

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);

        // ---------- 1️⃣ Create STT job ----------
        using var createForm = new MultipartFormDataContent();

        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);

        if (!string.IsNullOrWhiteSpace(request.MediaType))
            file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        createForm.Add(file, "audio", fileName);
        createForm.Add(new StringContent(model), "model");

        // if (!string.IsNullOrWhiteSpace(request.Language))
        //    createForm.Add(new StringContent(request.Language), "language");

        // if (request.Diarize == true)
        //    createForm.Add(new StringContent("true"), "diarize");

        using var createResp = await _client.PostAsync(
            $"v1/stt/create",
            createForm,
            cancellationToken);

        var createJson = await createResp.Content.ReadAsStringAsync(cancellationToken);

        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AIML STT create failed: {createJson}");

        using var createDoc = JsonDocument.Parse(createJson);
        var generationId = createDoc.RootElement.GetProperty("generation_id").GetString();

        if (string.IsNullOrWhiteSpace(generationId))
            throw new InvalidOperationException("AIML STT returned no generation_id");

        // ---------- 2️⃣ Poll for result ----------
        var start = DateTime.UtcNow;
        var timeout = TimeSpan.FromMinutes(10);

        while (DateTime.UtcNow - start < timeout)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);

            using var pollResp = await _client.GetAsync(
                $"v1/stt/{generationId}",
                cancellationToken);

            var pollJson = await pollResp.Content.ReadAsStringAsync(cancellationToken);

            if (!pollResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"AIML STT poll failed: {pollJson}");

            using var pollDoc = JsonDocument.Parse(pollJson);
            var root = pollDoc.RootElement;

            var status = root.GetProperty("status").GetString();

            if (status is "waiting" or "active")
                continue;

            // ---------- 3️⃣ Completed ----------
            return ConvertAIMLResponse(root, model, pollJson);
        }

        throw new TimeoutException("AIML STT timed out");
    }

    // ---------- Response mapping ----------
    private static TranscriptionResponse ConvertAIMLResponse(
        JsonElement root,
        string model,
        string rawJson)
    {
        var results =
            root.GetProperty("result")
                .GetProperty("results")
                .GetProperty("channels")[0]
                .GetProperty("alternatives")[0];

        var text = results.GetProperty("transcript").GetString() ?? "";

        var segments = new List<TranscriptionSegment>();

        if (results.TryGetProperty("words", out var wordsEl) &&
            wordsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in wordsEl.EnumerateArray())
            {
                segments.Add(new TranscriptionSegment
                {
                    Text = w.GetProperty("word").GetString() ?? "",
                    StartSecond = w.GetProperty("start").GetDouble(),
                    EndSecond = w.GetProperty("end").GetDouble()
                });
            }
        }

        return new TranscriptionResponse
        {
            Text = text,
            Segments = segments,
            Response = new ()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = model,
                Body = rawJson
            }
        };
    }
}