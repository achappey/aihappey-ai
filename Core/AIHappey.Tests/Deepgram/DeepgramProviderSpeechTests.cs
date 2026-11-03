using System.Net;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Deepgram;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Deepgram;

public sealed class DeepgramProviderSpeechTests
{
    [Theory]
    [InlineData(0.7)]
    [InlineData(1.15)]
    [InlineData(1.5)]
    public async Task Aura2AcceptsDoublePrecisionRangeBoundariesAndSteps(double speed)
    {
        Uri? uri = null;
        var provider = CreateProvider(request => { uri = request.RequestUri; return AudioResponse(); });
        await provider.SpeechRequest(new SpeechRequest { Model = "aura-2-thalia-en", Text = "Hello", Speed = speed });
        Assert.Contains("speed=" + speed.ToString(System.Globalization.CultureInfo.InvariantCulture), uri!.Query);
    }

    [Fact]
    public async Task Speech_request_forwards_speed_for_aura_2()
    {
        Uri? requestUri = null;
        var provider = CreateProvider(request =>
        {
            requestUri = request.RequestUri;
            return AudioResponse();
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "aura-2-thalia-en",
            Text = "Hello",
            Speed = 1.15f
        });

        Assert.Contains("speed=1.15", requestUri!.Query);
        Assert.Empty(response.Warnings);
    }

    [Fact]
    public async Task Speech_request_keeps_speed_unsupported_warning_for_non_aura_2_models()
    {
        Uri? requestUri = null;
        var provider = CreateProvider(request =>
        {
            requestUri = request.RequestUri;
            return AudioResponse();
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "aura-asteria-en",
            Text = "Hello",
            Speed = 1.15f
        });

        Assert.DoesNotContain("speed=", requestUri!.Query);
        Assert.Single(response.Warnings);
    }

    [Theory]
    [InlineData(0.69f)]
    [InlineData(1.51f)]
    [InlineData(1.13f)]
    public async Task Speech_request_rejects_unsupported_aura_2_speeds(float speed)
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "aura-2-thalia-en",
            Text = "Hello",
            Speed = speed
        }));
    }

    [Fact]
    public async Task Speech_request_preserves_known_mappings_and_forwards_unknown_provider_options()
    {
        Uri? requestUri = null;
        string? requestBody = null;
        var provider = CreateProvider(request =>
        {
            requestUri = request.RequestUri;
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return AudioResponse();
        });

        using var metadata = JsonDocument.Parse("""
            {
              "Encoding": "linear16",
              "sample_rate": 24000,
              "tag": ["first", "second"],
              "future_text": "hello world",
              "future_number": 3.5,
              "future_flag": true,
              "future_object": { "nested": [1, null, { "enabled": false }] },
              "future_array": ["item", 2, { "nested": [true, null] }],
              "future_null": null
            }
            """);

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "aura-asteria-en",
            Text = "Hello",
            OutputFormat = "wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["deepgram"] = metadata.RootElement.Clone()
            }
        });

        var query = ParseQuery(requestUri!.Query);
        Assert.Contains(new KeyValuePair<string, string>("model", "aura-asteria-en"), query);
        Assert.Contains(new KeyValuePair<string, string>("encoding", "linear16"), query);
        Assert.Contains(new KeyValuePair<string, string>("container", "wav"), query);
        Assert.Contains(new KeyValuePair<string, string>("sample_rate", "24000"), query);
        Assert.Equal(new[] { "first", "second" }, query.Where(parameter => parameter.Key == "tag").Select(parameter => parameter.Value));
        Assert.Contains(new KeyValuePair<string, string>("future_text", "hello world"), query);
        Assert.Contains(new KeyValuePair<string, string>("future_number", "3.5"), query);
        Assert.Contains(new KeyValuePair<string, string>("future_flag", "true"), query);
        Assert.Contains(new KeyValuePair<string, string>("future_null", "null"), query);

        using var nestedObject = JsonDocument.Parse(query.Single(parameter => parameter.Key == "future_object").Value);
        Assert.Equal(1, nestedObject.RootElement.GetProperty("nested")[0].GetInt32());
        Assert.Equal(JsonValueKind.Null, nestedObject.RootElement.GetProperty("nested")[1].ValueKind);
        Assert.False(nestedObject.RootElement.GetProperty("nested")[2].GetProperty("enabled").GetBoolean());

        var arrayValues = query.Where(parameter => parameter.Key == "future_array").Select(parameter => parameter.Value).ToArray();
        Assert.Equal(new[] { "item", "2", """{"nested":[true,null]}""" }, arrayValues);
        Assert.Equal("""{"text":"Hello"}""", requestBody);
    }

    [Fact]
    public async Task Speech_request_preserves_invalid_known_parameter_errors()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));
        using var invalidMetadata = JsonDocument.Parse("""{ "sample_rate": "invalid" }""");

        await Assert.ThrowsAsync<JsonException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "aura-asteria-en",
            Text = "Hello",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["deepgram"] = invalidMetadata.RootElement.Clone()
            }
        }));
    }

    [Fact]
    public async Task Speech_request_keeps_default_encoding_and_omits_unspecified_options()
    {
        Uri? requestUri = null;
        var provider = CreateProvider(request =>
        {
            requestUri = request.RequestUri;
            return AudioResponse();
        });

        await provider.SpeechRequest(new SpeechRequest { Model = "aura-asteria-en", Text = "Hello" });

        var query = ParseQuery(requestUri!.Query);
        Assert.Contains(new KeyValuePair<string, string>("encoding", "mp3"), query);
        Assert.DoesNotContain(query, parameter => parameter.Key == "container");
        Assert.DoesNotContain(query, parameter => parameter.Key == "sample_rate");
        Assert.DoesNotContain(query, parameter => parameter.Key == "bit_rate");
        Assert.DoesNotContain(query, parameter => parameter.Key == "callback");
    }

    private static DeepgramProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private static List<KeyValuePair<string, string>> ParseQuery(string query)
        => query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(parameter => parameter.Split('=', 2))
            .Select(parts => new KeyValuePair<string, string>(
                Uri.UnescapeDataString(parts[0]),
                parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty))
            .ToList();

    private static HttpResponseMessage AudioResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
            {
                Headers = { ContentType = new("audio/mpeg") }
            }
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
