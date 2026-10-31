using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.DeepInfra;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.DeepInfra;

public sealed class DeepInfraProviderTranscriptionTests
{
    [Fact]
    public async Task Transcription_forwards_known_and_unknown_provider_options_as_multipart_fields()
    {
        Dictionary<string, string> fields = [];
        using var client = CreateClient(async request =>
        {
            Assert.Equal("/v1/inference/org/asr", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer test-key", request.Headers.Authorization!.ToString());
            fields = await ReadFormFields(request.Content!);
            return JsonResponse("""{"text":"recognized","language":"en","duration":1.2}""");
        });
        var provider = CreateProvider(client);
        var request = new TranscriptionRequest
        {
            Model = "org/asr",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new()
            {
                ["deepinfra"] = ParseElement("""
                    {
                      "Task": "translate",
                      "initial_prompt": "names",
                      "temperature": "0.25",
                      "language": "fr",
                      "chunk_level": "word",
                      "chunk_length_s": 12,
                      "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
                      "future_array": [1, {"more": [false, 2.5]}, []],
                      "future_string": "unchanged",
                      "future_number": 12345678901234567890.123456789,
                      "future_boolean": false,
                      "future_null": null
                    }
                    """),
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };

        var response = await provider.TranscriptionRequest(request);

        Assert.Equal("recognized", response.Text);
        Assert.Equal("en", response.Language);
        Assert.Equal("translate", fields["task"]);
        Assert.Equal("names", fields["initial_prompt"]);
        Assert.Equal("0.25", fields["temperature"]);
        Assert.Equal("fr", fields["language"]);
        Assert.Equal("word", fields["chunk_level"]);
        Assert.Equal("12", fields["chunk_length_s"]);
        Assert.Equal("audio", fields["audio"]);
        Assert.Equal(
            """{"nested": [{"value": 1}, [true, null, "text"]]}""",
            fields["future_object"]);
        Assert.Equal("""[1, {"more": [false, 2.5]}, []]""", fields["future_array"]);
        Assert.Equal("unchanged", fields["future_string"]);
        Assert.Equal("12345678901234567890.123456789", fields["future_number"]);
        Assert.Equal("false", fields["future_boolean"]);
        Assert.Equal("null", fields["future_null"]);
        Assert.DoesNotContain("another-provider", fields.Keys);
        Assert.DoesNotContain("providerOptions", fields.Keys);
        Assert.Empty(response.Warnings);
    }

    [Fact]
    public async Task Transcription_preserves_null_and_empty_omission_and_chunk_length_warning()
    {
        Dictionary<string, string> fields = [];
        using var client = CreateClient(async request =>
        {
            fields = await ReadFormFields(request.Content!);
            return JsonResponse("""{"text":"recognized"}""");
        });
        var provider = CreateProvider(client);
        var request = new TranscriptionRequest
        {
            Model = "org/asr",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new()
            {
                ["deepinfra"] = ParseElement("""
                    {
                      "task": null,
                      "initial_prompt": "",
                      "temperature": null,
                      "language": " ",
                      "chunk_level": null,
                      "chunk_length_s": 31,
                      "future_null": null
                    }
                    """)
            }
        };

        var response = await provider.TranscriptionRequest(request);

        Assert.Equal(new[] { "audio", "future_null" }, fields.Keys.Order().ToArray());
        Assert.Equal("null", fields["future_null"]);
        Assert.Contains(response.Warnings, warning => warning.ToString()!.Contains("chunk_length_s", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Transcription_preserves_known_option_type_validation()
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var provider = CreateProvider(client);
        var request = new TranscriptionRequest
        {
            Model = "org/asr",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new()
            {
                ["deepinfra"] = ParseElement("""{"temperature":"warm"}""")
            }
        };

        await Assert.ThrowsAsync<JsonException>(() => provider.TranscriptionRequest(request));
    }

    [Fact]
    public async Task Transcription_preserves_upstream_error_behavior()
    {
        using var client = CreateClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid audio")
        }));
        var provider = CreateProvider(client);
        var request = new TranscriptionRequest
        {
            Model = "org/asr",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav"
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TranscriptionRequest(request));

        Assert.Equal("DeepInfra STT failed (400): invalid audio", error.Message);
    }

    private static DeepInfraProvider CreateProvider(HttpClient client)
        => new(new KeyResolver(), new ClientFactory(client), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new Handler(responder));

    private static async Task<Dictionary<string, string>> ReadFormFields(HttpContent content)
    {
        var body = await content.ReadAsStringAsync();
        var boundary = content.Headers.ContentType!.Parameters
            .Single(parameter => parameter.Name == "boundary")
            .Value!.Trim('"');
        Dictionary<string, string> fields = new(StringComparer.Ordinal);

        foreach (var part in body.Split($"--{boundary}", StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (separator < 0)
                continue;

            var dispositionLine = part.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase));
            var disposition = ContentDispositionHeaderValue.Parse(
                dispositionLine["Content-Disposition:".Length..].Trim());
            var name = disposition.Name!.Trim('"');
            fields[name] = part[(separator + 4)..].TrimEnd('\r', '\n');
        }

        return fields;
    }

    private static JsonElement ParseElement(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
