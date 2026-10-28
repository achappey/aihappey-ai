using System.Reflection;
using AIHappey.Core.Contracts;
using AIHappey.Core.MCP.Media;
using AIHappey.Core.MCP.Rerank;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests.MCP;

public sealed class MediaToolsUrlTests
{
    private const string AudioUrl = "https://media.example.test/audio.wav";
    private const string ImageOutputUrl = "https://192.0.2.1/image.png";

    [Theory]
    [InlineData("deapi")]
    [InlineData("spacexai")]
    [InlineData("token360")]
    public async Task Audio_transcription_passes_supported_urls_to_provider_without_downloading(string providerId)
    {
        var provider = CreateProvider(providerId);
        var factory = new RecordingHttpClientFactory(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        using var services = CreateServices(provider, factory);

        var result = await TranscriptionTools.AI_AudioTranscriptionsCreate(
            $"{providerId}/model",
            AudioUrl,
            null!,
            services);

        Assert.False(result!.IsError ?? false);
        Assert.Empty(factory.Requests);
        var request = Assert.IsType<TranscriptionRequest>(provider.TranscriptionRequest);
        Assert.Equal("model", request.Model);

        if (providerId == "deapi")
        {
            Assert.Equal(AudioUrl, request.Audio);
            Assert.Null(request.ProviderOptions);
        }
        else
        {
            Assert.Null(request.Audio);
            Assert.True(request.ProviderOptions!.TryGetValue(providerId, out var metadata));
            var name = providerId == "spacexai" ? "url" : "file_url";
            Assert.Equal(AudioUrl, metadata.GetProperty(name).GetString());
        }
    }

    [Fact]
    public async Task Audio_transcription_rejects_url_when_provider_does_not_support_it()
    {
        var provider = CreateProvider("other");
        var factory = new RecordingHttpClientFactory(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        using var services = CreateServices(provider, factory);

        var result = await TranscriptionTools.AI_AudioTranscriptionsCreate(
            "other/model",
            AudioUrl,
            null!,
            services);

        Assert.True(result!.IsError);
        Assert.Contains("does not natively support", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
        Assert.Null(provider.TranscriptionRequest);
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public async Task Reranking_url_tool_rejects_inputs_without_fetching_them()
    {
        var factory = new RecordingHttpClientFactory(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        using var services = CreateServices(null, factory);

        var result = await RerankingTools.AI_RerankUrls(
            "model",
            "query",
            [AudioUrl],
            null!,
            services);

        Assert.True(result!.IsError);
        Assert.Contains("are not downloaded", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public async Task Image_generation_downloads_and_materializes_provider_output_urls()
    {
        var provider = CreateProvider("image-provider");
        provider.ImageResponse = new ImageResponse { Images = [ImageOutputUrl] };
        var factory = new RecordingHttpClientFactory(_ =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return response;
        });
        using var services = CreateServices(provider, factory);

        var result = await ImageTools.AI_ImageGenerate(
            "image-provider/model",
            "test prompt",
            null!,
            services);

        Assert.False(result!.IsError ?? false);
        Assert.Equal([new Uri(ImageOutputUrl)], factory.Requests);
        Assert.Single(result.Content);
        Assert.IsType<ImageContentBlock>(result.Content[0]);
        Assert.False(result.StructuredContent!.Value.TryGetProperty("images", out _));
    }

    private static RecordingProvider CreateProvider(string providerId)
    {
        var provider = DispatchProxy.Create<IModelProvider, RecordingProvider>();
        var recording = (RecordingProvider)(object)provider;
        recording.ProviderId = providerId;
        return recording;
    }

    private static ServiceProvider CreateServices(RecordingProvider? provider, RecordingHttpClientFactory factory)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAIModelProviderResolver>(new StubResolver(
            provider is null ? null : (IModelProvider)(object)provider));
        services.AddSingleton<IHttpClientFactory>(factory);
        return services.BuildServiceProvider();
    }

    public class RecordingProvider : DispatchProxy
    {
        public string ProviderId { get; set; } = string.Empty;
        public TranscriptionRequest? TranscriptionRequest { get; private set; }
        public ImageResponse? ImageResponse { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                nameof(IModelProvider.GetIdentifier) => ProviderId,
                nameof(IModelProvider.TranscriptionRequest) => RecordTranscription((TranscriptionRequest)args![0]!),
                nameof(IModelProvider.ImageRequest) => Task.FromResult(ImageResponse ?? new ImageResponse { Images = [] }),
                _ => throw new NotSupportedException($"Unexpected provider call: {targetMethod?.Name}")
            };
        }

        private Task<TranscriptionResponse> RecordTranscription(TranscriptionRequest request)
        {
            TranscriptionRequest = request;
            return Task.FromResult(new TranscriptionResponse { Text = "transcribed" });
        }
    }

    private sealed class StubResolver(IModelProvider? provider) : IAIModelProviderResolver
    {
        public Task<IModelProvider> Resolve(string model, CancellationToken ct = default)
            => provider is null
                ? throw new InvalidOperationException("Unexpected provider resolution.")
                : Task.FromResult(provider);

        public IModelProvider GetProvider()
            => provider ?? throw new InvalidOperationException("Unexpected provider resolution.");

        public Task<ModelResponse> ResolveModels(CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class RecordingHttpClientFactory(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public List<Uri> Requests { get; } = [];

        public HttpClient CreateClient(string name)
            => new(new RecordingHandler(request =>
            {
                Requests.Add(request.RequestUri!);
                return responder(request);
            }));
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
