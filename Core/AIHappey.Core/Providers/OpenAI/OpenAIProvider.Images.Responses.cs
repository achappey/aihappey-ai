using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    private const string ImageResponsesOrchestrationModel = "gpt-6-luna";

    private static bool IsRemoteOpenAiImageInput(ImageFile file)
    {
        if (file is ImageFileUrl url)
        {
            ValidateOpenAiImageUrl(url.Url);
            return true;
        }

        if (string.Equals(file.Type, "url", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("URL image inputs must contain a 'url' property.");

        if (file.Data?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true)
        {
            ValidateOpenAiImageUrl(file.Data);
            return true;
        }
        return false;
    }

    private static string ValidateOpenAiImageUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Image URL inputs require an absolute public HTTP(S) URL without embedded credentials.");
        return url!;
    }

    private async Task<ImageResponse> RequestOpenAiImageViaResponsesAsync(
        ImageRequest request, List<ImageFile> files, List<object> warnings, DateTime timestamp,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        if (!IsGptOpenAiImageModel(request.Model))
            throw new NotSupportedException("URL image inputs through Responses require a GPT Image model.");

        var count = request.N ?? 1;
        if (count is < 1 or > 10)
            throw new ArgumentException("The number of images must be between 1 and 10.", nameof(request));

        var inputFiles = files.Take(16).ToList();
        if (files.Count > inputFiles.Count)
            warnings.Add(new { type = "unsupported", feature = "files", details = "OpenAI supports up to 16 input images. Used first 16 images." });

        var editMetadata = request.GetProviderMetadata<OpenAiImageEditProviderMetadata>(GetIdentifier());
        var imageMetadata = request.GetProviderMetadata<OpenAiImageProviderMetadata>(GetIdentifier());
        var toolOptions = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["action"] = "edit",
            ["output_format"] = DefaultOpenAiImageOutputFormat
        };
        AddImageResponseToolOption(toolOptions, "size", ResolveOpenAiImageSize(request, warnings, OpenAiImageOperation.Edit));
        AddImageResponseToolOption(toolOptions, "quality", editMetadata?.Quality);
        AddImageResponseToolOption(toolOptions, "background", editMetadata?.Background);
        AddImageResponseToolOption(toolOptions, "moderation", imageMetadata?.Moderation);
        if (!string.IsNullOrWhiteSpace(editMetadata?.InputFidelity))
        {
            if (request.Model.StartsWith("gpt-image-2", StringComparison.OrdinalIgnoreCase))
                warnings.Add(new { type = "unsupported", feature = "input_fidelity", details = "Input fidelity is not supported by this image model and was omitted." });
            else
                toolOptions["input_fidelity"] = editMetadata.InputFidelity;
        }
        if (request.Mask is not null)
            toolOptions["input_image_mask"] = ToOpenAiImageReference(request.Mask);

        List<ResponseContentPart> content = [new InputTextPart(request.Prompt)];
        foreach (var file in inputFiles)
        {
            var reference = ToOpenAiImageReference(file);
            content.Add(new InputImagePart
            {
                Detail = "auto",
                ImageUrl = reference.TryGetValue("image_url", out var url) ? (string?)url : null,
                FileId = reference.TryGetValue("file_id", out var id) ? (string?)id : null
            });
        }

        List<string> images = [];
        List<object> responsesMetadata = [];
        var inputTokens = 0;
        var outputTokens = 0;
        var totalTokens = 0;
        var hasUsage = false;
        // The Responses image tool has no n parameter. Execute one forced image call per result.
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await ResponsesAsync(new ResponseRequest
            {
                Model = ImageResponsesOrchestrationModel,
                Input = new ResponseInput([new ResponseInputMessage { Content = new ResponseMessageContent(content) }]),
                Tools = [new ResponseToolDefinition
                {
                    Type = "image_generation",
                    Extra = toolOptions.ToDictionary(entry => entry.Key,
                        entry => JsonSerializer.SerializeToElement(entry.Value, OpenAiImageJsonOptions))
                }],
                ToolChoice = new { type = "image_generation" },
                ParallelToolCalls = false,
                Stream = false,
                Store = false,
                AdditionalProperties = new() { ["max_tool_calls"] = JsonSerializer.SerializeToElement(1) }
            }, cancellationToken);

            if (response.Error is not null || response.Status != "completed")
                throw new InvalidOperationException($"OpenAI Responses image request did not complete: {response.Error?.Message ?? response.Status ?? "unknown status"}.");

            var completedImages = new List<string>();
            foreach (var output in response.Output)
            {
                var item = JsonSerializer.SerializeToElement(output, ResponseJson.Default);
                if (item.ValueKind != JsonValueKind.Object || ReadOpenAiImageString(item, "type") != "image_generation_call")
                    continue;
                if (ReadOpenAiImageString(item, "status") != "completed")
                    throw new InvalidOperationException("OpenAI Responses image-generation tool did not complete.");
                var result = ReadOpenAiImageString(item, "result");
                if (!string.IsNullOrWhiteSpace(result))
                    completedImages.Add(NormalizeOpenAiImageOutput(result,
                        OpenAiImageMediaTypeFromFormat(ReadOpenAiImageString(item, "output_format") ?? DefaultOpenAiImageOutputFormat)));
            }
            if (completedImages.Count != 1)
                throw new InvalidOperationException("OpenAI Responses image request must return exactly one completed image per call.");
            images.Add(completedImages[0]);

            var usageRoot = JsonSerializer.SerializeToElement(new { usage = response.Usage }, ResponseJson.Default);
            var usage = ExtractOpenAiImageUsage(usageRoot);
            if (usage is not null)
            {
                hasUsage = true;
                inputTokens += usage.InputTokens ?? 0;
                outputTokens += usage.OutputTokens ?? 0;
                totalTokens += usage.TotalTokens ?? 0;
            }
            responsesMetadata.Add(new
            {
                responseId = response.Id,
                orchestrationModel = response.Model,
                usage = response.Usage,
                metadata = response.Metadata
            });
        }

        // Responses usage/cost belongs to orchestration, not the Images API token-pricing formula.
        // Preserve existing Responses cost enrichment under each response without inventing image cost.
        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = hasUsage ? new ImageUsageData { InputTokens = inputTokens, OutputTokens = outputTokens, TotalTokens = totalTokens } : null,
            ProviderMetadata = new()
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    route = "responses", action = "edit", imageModel = request.Model, responses = responsesMetadata
                }, ResponseJson.Default)
            },
            Response = new HeaderResponseData { ModelId = request.Model.ToModelId(GetIdentifier()), Timestamp = timestamp }
        };
    }

    private static void AddImageResponseToolOption(Dictionary<string, object?> options, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            options[name] = value;
    }
}
