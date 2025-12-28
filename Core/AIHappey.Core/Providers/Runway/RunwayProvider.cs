using AIHappey.Core.AI;
using OAIC = OpenAI.Chat;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Common.Model.ChatCompletions;
using OpenAI.Responses;
using AIHappey.Common.Model;
using System.Net.Mime;
using System.Text.Json.Serialization;
using System.Text;
using System.Text.Json.Nodes;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Runway;

public partial class RunwayProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public RunwayProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.dev.runwayml.com/");
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        _client.DefaultRequestHeaders.Add("X-Runway-Version", "2024-11-06");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Runway)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public float? GetPriority() => 1;

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<OAIC.StreamingChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public string GetIdentifier() => "runway";

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default) =>
        await Task.FromResult<IEnumerable<Model>>(_keyResolver.Resolve(GetIdentifier()) != null ? [new Model()
            {
                OwnedBy = nameof(Runway),
                Name = "gen4_image",
                Type = "image",
                Id = "gen4_image".ToModelId(GetIdentifier())
            }, new Model()
            {
                OwnedBy = nameof(Runway),
                Type = "image",
                Name = "gen4_image_turbo",
                Id = "gen4_image_turbo".ToModelId(GetIdentifier())
            }, new Model()
            {
                OwnedBy = nameof(Runway),
                Type = "image",
                Name = "gemini_2.5_flash",
                Id = "gemini_2.5_flash".ToModelId(GetIdentifier())
            }] : []);

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ResponseResult> CreateResponseAsync(ResponseReasoningOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
       ChatRequest chatRequest,
       [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prompt = string.Join("\n", chatRequest.Messages?
            .LastOrDefault(m => m.Role == Common.Model.Role.user)
            ?.Parts?.OfType<TextUIPart>().Select(a => a.Text) ?? []);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            yield return "No prompt provided.".ToErrorUIPart();
            //yield return new FinishUIPart();
            yield break;
        }

        // 2. Build ImageRequest
        var imageRequest = new ImageRequest
        {
            Prompt = prompt,
            Model = chatRequest.Model,
        };

        ImageResponse? result = null;
        string? exceptionMessage = null;

        try
        {
            result = await ImageRequest(imageRequest, cancellationToken);
        }
        catch (Exception ex)
        {
            exceptionMessage = ex.Message;
        }

        if (!string.IsNullOrEmpty(exceptionMessage))
        {
            yield return exceptionMessage.ToErrorUIPart();
            yield break;
        }

        foreach (var image in result?.Images ?? [])
        {
            // image = "data:image/png;base64,AAAA..."
            var commaIndex = image.IndexOf(',');

            if (commaIndex <= 0)
                continue;

            var header = image[..commaIndex];              // data:image/png;base64
            var data = image[(commaIndex + 1)..];          // base64 payload

            var mediaType = header
                .Replace("data:", "", StringComparison.OrdinalIgnoreCase)
                .Replace(";base64", "", StringComparison.OrdinalIgnoreCase);

            yield return new FileUIPart
            {
                MediaType = mediaType,   // "image/png"
                Url = data              // keep full data URL
            };
        }

        // 4. Finish
        yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, null);
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string ExtractTaskId(JsonNode? json)
            => json?["id"]?.ToString() ?? throw new Exception("No task ID returned from Runway API.");

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        var now = DateTime.UtcNow;
        var metadata = imageRequest.GetImageProviderMetadata<RunwayImageProviderMetadata>(GetIdentifier());
        var payload = new
        {
            promptText = imageRequest.Prompt,
            model = imageRequest.Model,
            ratio = imageRequest.Size ?? "1024:1024",
            seed = imageRequest.Seed,
            contentModeration = metadata?.ContentModeration,
            referenceImages = imageRequest.Files?.Select(a => new
            {
                uri = a.Data.ToDataUrl(a.MediaType)
            })
        };

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        using var resp = await _client.PostAsync("v1/text_to_image",
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json), cancellationToken);
        var text = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {text}");

        var node = JsonNode.Parse(text);
        var taskId = ExtractTaskId(node);
        var images = await WaitForTaskAndUploadAsync(taskId, cancellationToken);

        return new()
        {
            Images = images,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model
            }
        };
    }

    /// <summary>
    /// Polls a Runway task until completion and uploads all output URLs to MCP storage.
    /// </summary>
    public async Task<IEnumerable<string>> WaitForTaskAndUploadAsync(
    //    this HttpC client,
        string taskId,
        CancellationToken ct = default)
    {
        string? status = null;
        JsonNode? json;
        List<string> results = [];

        do
        {
            using var resp = await _client.GetAsync($"v1/tasks/{taskId}", ct);
            var text = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"{resp.StatusCode}: {text}");

            json = JsonNode.Parse(text);
            status = json?["status"]?.ToString();

            if (status == "SUCCEEDED")
            {
                var outputs = json?["output"]?.AsArray();
                if (outputs == null || outputs.Count == 0)
                    throw new Exception("No outputs returned by Runway API.");

                //   var downloadService = sp.GetRequiredService<DownloadService>();
                //  var uploadedResources = new List<ContentBlock>();

                foreach (var output in outputs)
                {
                    var outputUrl = output?.ToString();
                    if (string.IsNullOrWhiteSpace(outputUrl))
                        continue;

                    var allItems = await _client.GetAsync(outputUrl, ct);
                    var result = await allItems.Content.ReadAsByteArrayAsync(ct);

                    results.Add(Convert.ToBase64String(result).ToDataUrl(allItems.Content.Headers.ContentType?.MediaType!));
                }

                if (results.Count == 0)
                    throw new Exception("No valid outputs could be uploaded.");

                return results;
            }

            if (status == "FAILED")
            {
                var reason = json?["failure"]?.ToString() ?? "Unknown failure.";
                var code = json?["failureCode"]?.ToString() ?? "";
                throw new Exception($"Runway task failed: {reason} ({code})");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), ct);

        } while (status != "SUCCEEDED" && status != "FAILED");

        throw new TimeoutException($"Runway task {taskId} did not complete.");
    }

}