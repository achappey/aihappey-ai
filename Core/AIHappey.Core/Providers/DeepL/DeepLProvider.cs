using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;

namespace AIHappey.Core.Providers.DeepL;

public partial class DeepLProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public DeepLProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api-free.deepl.com/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(DeepL)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key.Trim());
    }


    public string GetIdentifier() => nameof(DeepL).ToLowerInvariant();  

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
   
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    private sealed class DeepLLanguage
    {
        public string? Language { get; set; }
        public string? Name { get; set; }
        public bool? SupportsFormality { get; set; }
    }

    private sealed class DeepLTranslateResponse
    {
        public List<DeepLTranslation>? Translations { get; set; }
    }

    private sealed class DeepLTranslation
    {
        public string? Text { get; set; }
    }

    private static string ParseTargetLanguageFromModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("Model is required.", nameof(modelId));

        const string prefix = "translate-to/";
        var model = modelId.Trim();

        if (!model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"DeepL translation model must start with '{prefix}'. Got '{modelId}'.", nameof(modelId));

        var targetLanguage = model[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(targetLanguage))
            throw new ArgumentException("DeepL translation target language is missing from model id.", nameof(modelId));

        return targetLanguage;
    }


    private async Task<IReadOnlyList<string>> TranslateAsync(
        List<string> texts,
        string modelId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0) throw new ArgumentException("At least one text is required.", nameof(texts));

        var targetLanguage = ParseTargetLanguageFromModel(modelId);

        var payload = new Dictionary<string, object?>
        {
            ["text"] = texts,
            ["target_lang"] = targetLanguage
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "v2/translate")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeepL translate failed ({(int)resp.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<DeepLTranslateResponse>(body, JsonSerializerOptions.Web);
        var translations = parsed?.Translations ?? [];

        if (translations.Count == 0)
            return [.. texts.Select(_ => string.Empty)];

        var result = new List<string>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            var text = (i < translations.Count)
                ? translations[i].Text
                : null;

            result.Add(text ?? string.Empty);
        }

        return result;
    }




}
