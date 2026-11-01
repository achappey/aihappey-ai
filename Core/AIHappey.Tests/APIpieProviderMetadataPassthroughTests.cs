using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.APIpie;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class APIpieProviderMetadataPassthroughTests
{
    [Fact]
    public async Task Speech_request_merges_unknown_provider_options_at_root_and_preserves_known_precedence()
    {
        string? body = null;
        using var client = CreateClient(async request =>
        {
            Assert.Equal("/v1/audio/speech", request.RequestUri!.AbsolutePath);
            body = await request.Content!.ReadAsStringAsync();
            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio"))
            };
        });
        var metadata = ParseElement("""
            {
              "provider": "elevenlabs",
              "voice": "metadata-voice",
              "voiceSettings": {
                "Stability": 0.6,
                "style": null,
                "vendor_nested": {"enabled": true, "values": [1, null, "kept"], "future_null": null}
              },
              "responseFormat": "wav",
              "model": "metadata-model",
              "input": "metadata-input",
              "speed": 0.25,
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        var request = new SpeechRequest
        {
            Model = "request-model",
            Text = "request text",
            Voice = "request-voice",
            OutputFormat = "mp3",
            Speed = 1.5f,
            ProviderOptions = new()
            {
                ["apipie"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };
        var before = JsonSerializer.Serialize(request);

        await CreateProvider(client).SpeechRequest(request);

        using var payload = JsonDocument.Parse(Assert.IsType<string>(body));
        var root = payload.RootElement;
        Assert.Equal("elevenlabs", root.GetProperty("provider").GetString());
        Assert.Equal("request-model", root.GetProperty("model").GetString());
        Assert.Equal("request text", root.GetProperty("input").GetString());
        Assert.Equal("request-voice", root.GetProperty("voice").GetString());
        Assert.Equal("mp3", root.GetProperty("responseFormat").GetString());
        Assert.Equal(1.5, root.GetProperty("speed").GetDouble());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(0.6, root.GetProperty("voiceSettings").GetProperty("stability").GetDouble());
        Assert.False(root.GetProperty("voiceSettings").TryGetProperty("Stability", out _));
        Assert.False(root.GetProperty("voiceSettings").TryGetProperty("style", out _));
        Assert.True(root.GetProperty("voiceSettings").GetProperty("vendor_nested")
            .GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null,
            root.GetProperty("voiceSettings").GetProperty("vendor_nested").GetProperty("values")[1].ValueKind);
        Assert.Equal(JsonValueKind.Null,
            root.GetProperty("voiceSettings").GetProperty("vendor_nested").GetProperty("future_null").ValueKind);
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), root.GetProperty(name)), name);
        }

        Assert.False(root.TryGetProperty("providerOptions", out _));
        Assert.False(root.TryGetProperty("apipie", out _));
        Assert.False(root.TryGetProperty("another-provider", out _));
        Assert.False(root.TryGetProperty("secret", out _));
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public async Task Speech_request_keeps_defaults_omission_validation_and_upstream_errors()
    {
        string? body = null;
        using var client = CreateClient(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio"))
            };
        });

        await CreateProvider(client).SpeechRequest(new SpeechRequest
        {
            Model = "model",
            Text = "text"
        });

        using (var payload = JsonDocument.Parse(Assert.IsType<string>(body)))
        {
            var root = payload.RootElement;
            Assert.Equal("openai", root.GetProperty("provider").GetString());
            Assert.Equal("alloy", root.GetProperty("voice").GetString());
            Assert.Equal("mp3", root.GetProperty("responseFormat").GetString());
            Assert.False(root.TryGetProperty("voiceSettings", out _));
            Assert.False(root.TryGetProperty("speed", out _));
            Assert.Equal(6, root.EnumerateObject().Count());
        }

        using var invalidClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(invalidClient).SpeechRequest(new SpeechRequest
        {
            Model = "model",
            Text = "text",
            ProviderOptions = Options("""{"voice":42}""")
        }));

        using var failedClient = CreateClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(failedClient).SpeechRequest(new SpeechRequest { Model = "model", Text = "text" }));
        Assert.Contains("APIpie TTS failed (400): upstream failure", error.Message);
    }

    [Fact]
    public async Task Transcription_request_forwards_unknown_multipart_fields_and_maps_known_options()
    {
        List<(string Name, string Value)>? fields = null;
        using var client = CreateClient(async request =>
        {
            Assert.Equal("/v1/audio/transcriptions", request.RequestUri!.AbsolutePath);
            fields = await ReadMultipartFields(request.Content!);
            return JsonResponse("""{"text":"recognized"}""");
        });
        var metadata = ParseElement("""
            {
              "prompt": "metadata prompt",
              "language": "en",
              "temperature": 0.25,
              "responseFormat": "json",
              "timestampGranularities": ["word", "segment"],
              "model": "metadata-model",
              "file": "metadata-file",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 123,
              "future_boolean": false,
              "future_null": null,
              "future field": "quoted name"
            }
            """);
        var request = new TranscriptionRequest
        {
            Model = "request-model",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new()
            {
                ["apipie"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };
        var before = JsonSerializer.Serialize(request);

        await CreateProvider(client).TranscriptionRequest(request);

        var multipart = Assert.IsType<List<(string Name, string Value)>>(fields);
        AssertMultipartField(multipart, "file", "audio");
        AssertMultipartField(multipart, "model", "request-model");
        AssertMultipartField(multipart, "prompt", "metadata prompt");
        AssertMultipartField(multipart, "language", "en");
        AssertMultipartField(multipart, "temperature", "0.25");
        AssertMultipartField(multipart, "response_format", "verbose_json");
        Assert.Equal(new[] { "word", "segment" },
            multipart.Where(field => field.Name == "timestamp_granularities").Select(field => field.Value));
        AssertMultipartField(multipart, "future_object", metadata.GetProperty("future_object").GetRawText());
        AssertMultipartField(multipart, "future_array", metadata.GetProperty("future_array").GetRawText());
        AssertMultipartField(multipart, "future_string", "unchanged");
        AssertMultipartField(multipart, "future_number", "123");
        AssertMultipartField(multipart, "future_boolean", "false");
        AssertMultipartField(multipart, "future_null", "null");
        AssertMultipartField(multipart, "future field", "quoted name");
        Assert.Equal(new[]
            {
                "file", "model", "prompt", "language", "temperature", "response_format",
                "timestamp_granularities", "timestamp_granularities",
                "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null",
                "future field"
            }.Order(),
            multipart.Select(field => field.Name).Order());
        var clientPayload = JsonSerializer.SerializeToElement(request);
        Assert.True(JsonElement.DeepEquals(metadata, clientPayload.GetProperty("providerOptions").GetProperty("apipie")));
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public async Task Transcription_request_keeps_omissions_validation_and_error_details()
    {
        List<(string Name, string Value)>? fields = null;
        using var client = CreateClient(async request =>
        {
            fields = await ReadMultipartFields(request.Content!);
            return JsonResponse("""{"text":"recognized"}""");
        });

        await CreateProvider(client).TranscriptionRequest(new TranscriptionRequest
        {
            Model = "model",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = Options(
                """{"prompt":null,"language":"","temperature":null,"responseFormat":null,"timestampGranularities":null,"future_null":null}""")
        });

        var multipart = Assert.IsType<List<(string Name, string Value)>>(fields);
        AssertMultipartField(multipart, "file", "audio");
        AssertMultipartField(multipart, "model", "model");
        AssertMultipartField(multipart, "future_null", "null");
        Assert.Equal(new[] { "file", "model", "future_null" }.Order(),
            multipart.Select(field => field.Name).Order());

        using var invalidClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(invalidClient).TranscriptionRequest(
            new TranscriptionRequest
            {
                Model = "model",
                Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
                MediaType = "audio/wav",
                ProviderOptions = Options("""{"temperature":"bad"}""")
            }));

        using var failedClient = CreateClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(failedClient).TranscriptionRequest(new TranscriptionRequest
            {
                Model = "model",
                Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
                MediaType = "audio/wav"
            }));
        Assert.Contains("APIpie STT failed (400): upstream failure", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    public async Task Transcription_request_without_options_omits_optional_fields(string? metadata)
    {
        List<(string Name, string Value)>? fields = null;
        using var client = CreateClient(async request =>
        {
            fields = await ReadMultipartFields(request.Content!);
            return JsonResponse("""{"text":"recognized"}""");
        });

        await CreateProvider(client).TranscriptionRequest(new TranscriptionRequest
        {
            Model = "model",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = metadata is null ? null : Options(metadata)
        });

        Assert.Equal(new[] { ("file", "audio"), ("model", "model") }, fields);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Multipart_assertions_parse_quoted_and_unquoted_names_and_preserve_values(bool quoted)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(" exact value\r\n"), "prompt");
        form.Add(new StringContent("word"), "timestamp_granularities");
        form.Add(new StringContent("segment"), "timestamp_granularities");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("audio")), "file", "audio.wav");
        foreach (var part in form)
        {
            var disposition = part.Headers.ContentDisposition!;
            var name = disposition.Name!.Trim('"');
            disposition.Name = quoted ? $"\"{name}\"" : name;
        }

        var fields = await ReadMultipartFields(form);

        Assert.Equal(new[]
        {
            ("prompt", " exact value\r\n"), ("timestamp_granularities", "word"),
            ("timestamp_granularities", "segment"), ("file", "audio")
        }, fields);
    }

    private static APIpieProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));

    private static Dictionary<string, JsonElement> Options(string json)
        => new() { ["apipie"] = ParseElement(json) };

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<List<(string Name, string Value)>> ReadMultipartFields(HttpContent content)
    {
        Assert.Equal("multipart/form-data", content.Headers.ContentType!.MediaType);
        var boundary = content.Headers.ContentType.Parameters
            .Single(parameter => parameter.Name == "boundary").Value!.Trim('"');
        using var stream = new MemoryStream(await content.ReadAsByteArrayAsync());
        var reader = new MultipartReader(boundary, stream);
        List<(string Name, string Value)> fields = [];
        while (await reader.ReadNextSectionAsync() is { } section)
        {
            var disposition = ContentDispositionHeaderValue.Parse(section.ContentDisposition!);
            Assert.Equal("form-data", disposition.DispositionType);
            using var valueReader = new StreamReader(section.Body, Encoding.UTF8, leaveOpen: true);
            fields.Add((Assert.IsType<string>(disposition.Name).Trim('"'), await valueReader.ReadToEndAsync()));
        }

        return fields;
    }

    private static void AssertMultipartField(List<(string Name, string Value)> fields, string name, string value)
        => Assert.Equal(value, Assert.Single(fields, field => field.Name == name).Value);

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return responder(request);
        }
    }
}
