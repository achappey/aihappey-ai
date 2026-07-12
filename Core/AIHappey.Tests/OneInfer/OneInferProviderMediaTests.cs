using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.OneInfer;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.OneInfer;

public sealed class OneInferProviderMediaTests
{
    [Fact]
    public async Task ImageRequest_merges_provider_options_and_downloads_urls()
    {
        HttpRequestMessage? capturedRequest = null;
        var imageBytes = Encoding.UTF8.GetBytes("png-bytes");
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/ula/generate-image")
            {
                capturedRequest = CloneRequest(request);
                return JsonResponse(new
                {
                    data = new
                    {
                        id = "img_123",
                        created = 1711468800,
                        images = new[]
                        {
                            new { url = "https://media.oneinfer.test/image.png", revised_prompt = (string?)null }
                        },
                        provider = "openai",
                        model = "dall-e-3",
                        usage = new { prompt_tokens = 12, completion_tokens = 0, total_tokens = 12 }
                    },
                    error = new { }
                });
            }

            if (request.RequestUri?.AbsoluteUri == "https://media.oneinfer.test/image.png")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(imageBytes)
                };
                response.Content.Headers.ContentType = new(MediaTypeNames.Image.Png);
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var response = await provider.ImageRequest(new ImageRequest
        {
            Model = "dall-e-3",
            Prompt = "A Bauhaus style villa.",
            Size = "1024x1024",
            N = 1,
            ProviderOptions = ProviderOptions(new
            {
                provider = "openai",
                quality = "hd"
            })
        });

        Assert.NotNull(capturedRequest);
        using var payloadDocument = JsonDocument.Parse(await capturedRequest!.Content!.ReadAsStringAsync());
        var payload = payloadDocument.RootElement;
        Assert.Equal("openai", payload.GetProperty("provider").GetString());
        Assert.Equal("dall-e-3", payload.GetProperty("model").GetString());
        Assert.Equal("hd", payload.GetProperty("quality").GetString());
        Assert.Equal("1024x1024", payload.GetProperty("size").GetString());
        Assert.Equal(1, payload.GetProperty("number").GetInt32());
        Assert.Equal("user", payload.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("A Bauhaus style villa.", payload.GetProperty("messages")[0].GetProperty("content").GetString());

        var image = Assert.Single(response.Images ?? []);
        Assert.Equal(Convert.ToBase64String(imageBytes).ToDataUrl(MediaTypeNames.Image.Png), image);
        Assert.True(response.ProviderMetadata?.ContainsKey("oneinfer"));
        Assert.Equal(12, response.Usage?.TotalTokens);
        Assert.Equal("oneinfer/dall-e-3", response.Response.ModelId);
    }

    [Fact]
    public async Task SpeechRequest_merges_provider_options_and_returns_base64_audio()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return JsonResponse(new
            {
                data = new
                {
                    id = "aud_123",
                    created = 1711468800,
                    text = "Generated audio for prompt: Hello.",
                    provider = "sarvam",
                    model = "bulbul:v3",
                    audios = new[]
                    {
                        new
                        {
                            url = "data:audio/mpeg;base64,YXVkaW8=",
                            format = "mp3",
                            base64_data = "YXVkaW8=",
                            mime_type = "audio/mpeg"
                        }
                    }
                },
                error = new { }
            });
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "bulbul:v3",
            Text = "Hello.",
            Voice = "shubh",
            OutputFormat = "mp3",
            Speed = 1.1f,
            ProviderOptions = ProviderOptions(new
            {
                provider = "sarvam",
                pitch = 0,
                volume = 1.0
            })
        });

        Assert.NotNull(capturedRequest);
        Assert.Equal("/v1/ula/generate-audio", capturedRequest!.RequestUri?.AbsolutePath);
        using var payloadDocument = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var payload = payloadDocument.RootElement;
        Assert.Equal("sarvam", payload.GetProperty("provider").GetString());
        Assert.Equal("bulbul:v3", payload.GetProperty("model").GetString());
        Assert.Equal("Hello.", payload.GetProperty("prompt").GetString());
        Assert.Equal("shubh", payload.GetProperty("voice_id").GetString());
        Assert.Equal("mp3", payload.GetProperty("format").GetString());
        Assert.Equal(0, payload.GetProperty("pitch").GetInt64());
        Assert.False(payload.GetProperty("stream").GetBoolean());

        Assert.Equal("YXVkaW8=", response.Audio.Base64);
        Assert.Equal("audio/mpeg", response.Audio.MimeType);
        Assert.Equal("mp3", response.Audio.Format);
        Assert.True(response.ProviderMetadata?.ContainsKey("oneinfer"));
    }

    [Fact]
    public async Task TranscriptionRequest_posts_multipart_audio_and_maps_text()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return JsonResponse(new
            {
                data = new
                {
                    id = "trn_123",
                    created = 1776233397,
                    text = "Computers can only help us with simple tasks.",
                    provider = "sarvam",
                    model = "saaras:v3",
                    usage = new { prompt_tokens = 9, completion_tokens = 0, total_tokens = 9 }
                },
                error = new { }
            });
        });

        var response = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "saaras:v3",
            MediaType = "audio/mpeg",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio-bytes")),
            ProviderOptions = ProviderOptions(new
            {
                provider = "sarvam",
                language = "en-IN"
            })
        });

        Assert.NotNull(capturedRequest);
        Assert.Equal("/v1/ula/generate-audio", capturedRequest!.RequestUri?.AbsolutePath);
        Assert.StartsWith("multipart/form-data", capturedRequest.Content!.Headers.ContentType?.MediaType);
        var multipart = await capturedRequest.Content.ReadAsStringAsync();
        Assert.Contains("name=model", multipart);
        Assert.Contains("saaras:v3", multipart);
        Assert.Contains("name=provider", multipart);
        Assert.Contains("sarvam", multipart);
        Assert.Contains("name=language", multipart);
        Assert.Contains("en-IN", multipart);
        Assert.Contains("filename=audio.mp3", multipart);

        Assert.Equal("Computers can only help us with simple tasks.", response.Text);
        Assert.Equal("en-IN", response.Language);
        Assert.True(response.ProviderMetadata?.ContainsKey("oneinfer"));
        Assert.Equal("oneinfer/saaras:v3", response.Response.ModelId);
    }

    [Fact]
    public async Task VideoRequest_merges_provider_options_and_downloads_video()
    {
        HttpRequestMessage? capturedRequest = null;
        var videoBytes = Encoding.UTF8.GetBytes("mp4-bytes");
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/ula/generate-video")
            {
                capturedRequest = CloneRequest(request);
                return JsonResponse(new
                {
                    data = new
                    {
                        id = "vid_123",
                        created = 1774529224,
                        provider = "novita",
                        model = "seedance-v1.5-pro-t2v",
                        videos = new[]
                        {
                            new { url = "https://media.oneinfer.test/video.mp4", type = "mp4" }
                        }
                    },
                    error = new { }
                });
            }

            if (request.RequestUri?.AbsoluteUri == "https://media.oneinfer.test/video.mp4")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(videoBytes)
                };
                response.Content.Headers.ContentType = new("video/mp4");
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var response = await provider.VideoRequest(new VideoRequest
        {
            Model = "seedance-v1.5-pro-t2v",
            Prompt = "A cat drinking milk.",
            Resolution = "720P",
            AspectRatio = "16:9",
            Duration = 5,
            Fps = 24,
            Seed = -1,
            ProviderOptions = ProviderOptions(new
            {
                provider = "novita",
                generate_audio = true,
                camera_fixed = false,
                service_tier = "default"
            })
        });

        Assert.NotNull(capturedRequest);
        using var payloadDocument = JsonDocument.Parse(await capturedRequest!.Content!.ReadAsStringAsync());
        var payload = payloadDocument.RootElement;
        Assert.Equal("novita", payload.GetProperty("provider").GetString());
        Assert.Equal("seedance-v1.5-pro-t2v", payload.GetProperty("model").GetString());
        Assert.Equal("A cat drinking milk.", payload.GetProperty("prompt").GetString());
        Assert.Equal("720P", payload.GetProperty("resolution").GetString());
        Assert.Equal("16:9", payload.GetProperty("aspect_ratio").GetString());
        Assert.Equal(5, payload.GetProperty("duration").GetInt32());
        Assert.Equal(24, payload.GetProperty("fps").GetInt32());
        Assert.True(payload.GetProperty("generate_audio").GetBoolean());
        Assert.False(payload.GetProperty("camera_fixed").GetBoolean());
        Assert.Equal("default", payload.GetProperty("service_tier").GetString());

        var video = Assert.Single(response.Videos ?? []);
        Assert.Equal(Convert.ToBase64String(videoBytes), video.Data);
        Assert.Equal("video/mp4", video.MediaType);
        Assert.True(response.ProviderMetadata?.ContainsKey("oneinfer"));
    }

    private static OneInferProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new OneInferProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
    }

    private static Dictionary<string, JsonElement> ProviderOptions(object metadata)
        => new()
        {
            ["oneinfer"] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web)
        };

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (request.Content is not null)
        {
            var content = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(content, Encoding.UTF8, request.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Application.Json);
            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider)
            => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
