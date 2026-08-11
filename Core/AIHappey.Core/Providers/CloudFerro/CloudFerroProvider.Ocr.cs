using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.CloudFerro;

public partial class CloudFerroProvider
{
    private const string OcrModelId = "olmOCR-7B-0225-preview";

    private static bool IsOcrModel(string? model)
        => string.Equals(model, OcrModelId, StringComparison.Ordinal)
            || string.Equals(model, $"cloudferro/{OcrModelId}", StringComparison.Ordinal);

    private async Task<AIResponse> ExecuteOcrUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var files = GetLatestUserFiles(request);
        ApplyAuthHeader();

        var output = new List<AIOutputItem>(files.Count);
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var bytes = DecodeOcrFile(file, index);
            var filename = string.IsNullOrWhiteSpace(file.Filename) ? $"document-{index + 1}" : file.Filename!;
            var mediaType = string.IsNullOrWhiteSpace(file.MediaType)
                ? "application/octet-stream"
                : file.MediaType!;

            using var form = new MultipartFormDataContent();
            using var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            form.Add(fileContent, "file", filename);

            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"../vision/v1/ocr/{Uri.EscapeDataString(OcrModelId)}")
            {
                Content = form
            };
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));

            using var response = await _client.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var markdown = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"CloudFerro OCR failed for '{filename}' ({(int)response.StatusCode}): {markdown}");

            output.Add(new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Metadata = new Dictionary<string, object?>
                {
                    ["filename"] = filename,
                    ["mediaType"] = mediaType,
                    ["fileIndex"] = index
                },
                Content = [new AITextContentPart { Text = markdown, Type = "text" }]
            });
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = OcrModelId,
            Status = "completed",
            Usage = new Dictionary<string, object?>(),
            Metadata = new Dictionary<string, object?>
            {
                ["finishReason"] = "stop",
                ["fileCount"] = files.Count
            },
            Output = new AIOutput { Items = output }
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamOcrUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecuteOcrUnifiedAsync(request, cancellationToken);

        foreach (var item in response.Output?.Items ?? [])
        {
            var eventId = Guid.NewGuid().ToString("n");
            var timestamp = DateTimeOffset.UtcNow;
            yield return CreateOcrStreamEvent(eventId, "text-start", new AITextStartEventData(), timestamp, item.Metadata);

            foreach (var text in (item.Content ?? []).OfType<AITextContentPart>())
            {
                if (!string.IsNullOrEmpty(text.Text))
                    yield return CreateOcrStreamEvent(
                        eventId,
                        "text-delta",
                        new AITextDeltaEventData { Delta = text.Text },
                        timestamp,
                        item.Metadata);
            }

            yield return CreateOcrStreamEvent(eventId, "text-end", new AITextEndEventData(), timestamp, item.Metadata);
        }

        var completedAt = DateTimeOffset.UtcNow;
        yield return CreateOcrStreamEvent(
            Guid.NewGuid().ToString("n"),
            "finish",
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = response.Model,
                CompletedAt = completedAt.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(response.Model ?? OcrModelId, completedAt, response.Usage)
            },
            completedAt,
            response.Metadata);
    }

    private static List<AIFileContentPart> GetLatestUserFiles(AIRequest request)
    {
        var latestUserMessage = request.Input?.Items?
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var files = latestUserMessage?.Content?.OfType<AIFileContentPart>().ToList() ?? [];

        if (files.Count == 0)
            throw new ArgumentException(
                "CloudFerro OCR requires at least one file in the latest user message.",
                nameof(request));

        return files;
    }

    private static byte[] DecodeOcrFile(AIFileContentPart file, int index)
    {
        var value = file.Data switch
        {
            string text => text,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
            _ => throw new ArgumentException(
                $"CloudFerro OCR file {index + 1} must contain base64 text or a base64 data URL.",
                nameof(file))
        };

        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"CloudFerro OCR file {index + 1} is empty.", nameof(file));

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"CloudFerro OCR file {index + 1} cannot be a remote URL.",
                nameof(file));

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0 || !value[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"CloudFerro OCR file {index + 1} must use a base64 data URL.",
                    nameof(file));

            value = value[(comma + 1)..];
        }

        try
        {
            return Convert.FromBase64String(value.Trim());
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                $"CloudFerro OCR file {index + 1} contains invalid base64 data.",
                nameof(file),
                exception);
        }
    }

    private AIStreamEvent CreateOcrStreamEvent(
        string eventId,
        string type,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Metadata = metadata,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = eventId,
                Timestamp = timestamp,
                Data = data,
                Metadata = metadata
            }
        };
}
