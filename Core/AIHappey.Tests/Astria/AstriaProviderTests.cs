using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Astria;
using AIHappey.HeaderAuth;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Astria;

public sealed class AstriaProviderTests
{
    private const string Catalog = """
        {"default":"native-image","models":{
          "native-image":{"tune_id":42,"resolution":"2K"},
          "alias":{"tune_id":42},"another":{"tune_id":"43"},
          "bad":{"tune_id":0},"null":null,"missing":{}},
         "video_model_families":{"native-video":{"title":"Native Video","resolutions":["720p","1080p"],
          "default_resolution":"720p","generates_audio":true},"invalid":null},
         "video_models":{"legacy-720p":{}},"extra":{"server_owned":true}}
        """;
    private const string Completed = """{"id":9,"images":["https://cdn.example.com/image.png"],"cost_mc":123,"user_error":null}""";

    [Fact]
    public void Registration_is_scoped_and_header_is_resolvable()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        var registration = Assert.Single(services, x => !x.IsKeyedService && x.ImplementationType == typeof(AstriaProvider));
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
        Assert.Equal(typeof(AstriaProvider), registration.ServiceType);
        Assert.Contains(services, x => x.IsKeyedService && x.ServiceType == typeof(IModelProvider)
            && Equals(x.ServiceKey, "astria") && x.Lifetime == ServiceLifetime.Scoped);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Astria-Key"] = "header-test-key";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("header-test-key", resolver.Resolve("astria"));
    }

    [Fact]
    public async Task Discovery_uses_public_cached_catalog_and_deduplicates_executable_ids()
    {
        var calls = 0;
        var provider = Create(request =>
        {
            calls++;
            Assert.Equal("/cli/models", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            return Json(Catalog);
        });
        var models = (await provider.ListModels()).ToArray();
        await provider.ListModels();
        Assert.Equal(1, calls);
        Assert.Equal(["astria/42", "astria/43", "astria/native-video"], models.Select(x => x.Id));
        Assert.Equal(["image", "image", "video"], models.Select(x => x.Type));
        Assert.Equal("native-image", models[0].Name);
        Assert.Equal("2K", models[0].Metadata!["native"].GetProperty("resolution").GetString());
        Assert.All(models, model => Assert.Equal("native", Assert.Single(model.Metadata!).Key));
        Assert.Equal("720p", models[2].Metadata!["native"].GetProperty("default_resolution").GetString());
    }

    [Fact]
    public async Task Missing_key_lists_nothing_and_never_submits_generation()
    {
        var calls = 0;
        var provider = Create(_ => { calls++; return Json(Catalog); }, key: null);
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ImageRequest(Image()));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Discovery_cancellation_is_honored_even_for_cached_catalog()
    {
        var provider = Create(_ => Json(Catalog));
        await provider.ListModels();
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(source.Token));
    }

    [Fact]
    public async Task Image_resolves_alias_forwards_all_native_fields_and_polls_until_images()
    {
        Dictionary<string, string[]>? fields = null;
        var polls = 0;
        var posts = 0;
        var provider = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/cli/models") return Json(Catalog);
            Assert.Equal(new AuthenticationHeaderValue("Bearer", "test-api-key"), request.Headers.Authorization);
            if (request.Method == HttpMethod.Post)
            {
                posts++;
                Assert.Equal("/tunes/42/prompts", request.RequestUri.AbsolutePath);
                fields = Fields(request);
                return Json("""[{"id":9,"images":[],"trained_at":"already-trained"}]""");
            }
            Assert.Equal("/tunes/42/prompts/9", request.RequestUri.AbsolutePath);
            polls++;
            return polls == 1 ? Json("""{"id":9,"images":[],"trained_at":"not-terminal"}""") : Json(Completed);
        }, download: request =>
        {
            Assert.Equal("cdn.example.com", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            return Media("image/png");
        });
        var request = Image("astria/native-image");
        request.N = 2;
        request.Seed = 0;
        request.Size = "1024x768";
        request.AspectRatio = "4:3";
        request.ProviderOptions = Native(new
        {
            num_images = 8, text = "native", seed = 7, steps = 0, film_grain = false,
            custom = new { strength = 0.25, enabled = false }, image_reference_urls = new[] { "one", "two" }
        });
        var result = await provider.ImageRequest(request);
        Assert.Equal(1, posts);
        Assert.Equal(2, polls);
        Assert.Equal("A mountain", Field(fields!, "text"));
        Assert.Equal("2", Field(fields!, "num_images"));
        Assert.Equal("0", Field(fields!, "seed"));
        Assert.Equal("1024", Field(fields!, "w"));
        Assert.Equal("768", Field(fields!, "h"));
        Assert.Equal("4:3", Field(fields!, "aspect_ratio"));
        Assert.Equal("false", Field(fields!, "film_grain"));
        Assert.Equal("0", Field(fields!, "steps"));
        Assert.Equal("0.25", Field(fields!, "custom][strength"));
        Assert.Equal("false", Field(fields!, "custom][enabled"));
        Assert.Equal(["one", "two"], fields!["prompt[image_reference_urls][]"]);
        Assert.Equal("data:image/png;base64,bWVkaWE=", Assert.Single(result.Images!));
        Assert.Equal(123, result.ProviderMetadata!["astria"].GetProperty("cost_mc").GetInt32());
    }

    [Fact]
    public async Task Omitted_normalized_fields_leave_native_defaults_intact()
    {
        var provider = Create(request =>
        {
            var fields = Fields(request);
            Assert.Equal(["prompt[num_images]", "prompt[seed]", "prompt[text]"], fields.Keys.Order());
            Assert.Equal("3", Field(fields, "num_images"));
            Assert.Equal("17", Field(fields, "seed"));
            return Json(Completed);
        });
        var request = Image();
        request.ProviderOptions = Native(new { num_images = 3, seed = 17 });
        await provider.ImageRequest(request);
    }

    [Fact]
    public async Task Image_edits_send_binary_input_and_remote_mask()
    {
        var provider = Create(request =>
        {
            var fields = Fields(request);
            Assert.Equal("image", Field(fields, "input_image"));
            Assert.Equal("https://inputs.example.com/mask.png", Field(fields, "mask_image_url"));
            return Json(Completed);
        });
        var request = Image();
        request.Files = [new ImageFile { MediaType = "image/png", Data = "data:image/png;base64,aW1hZ2U=" }];
        request.Mask = new ImageFileUrl { Url = "https://inputs.example.com/mask.png" };
        await provider.ImageRequest(request);
    }

    [Fact]
    public async Task Multiple_image_inputs_are_rejected_before_posting()
    {
        var provider = Create(_ => throw new Xunit.Sdk.XunitException("Must not call API."));
        var request = Image();
        request.Files = [new ImageFile(), new ImageFile()];
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ImageRequest(request));
    }

    [Theory]
    [InlineData("""{"id":9,"user_error":"provider validation failed","images":[]}""", "provider validation failed")]
    [InlineData("""{"images":[]}""", "valid id")]
    [InlineData("""[]""", "number of prompts")]
    public async Task Invalid_or_failed_prompt_responses_are_not_retried(string body, string message)
    {
        var posts = 0;
        var provider = Create(_ => { posts++; return Json(body); });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ImageRequest(Image()));
        Assert.Contains(message, error.Message);
        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task Polling_failure_exposes_user_error()
    {
        var provider = Create(request => Json(request.Method == HttpMethod.Post
            ? """{"id":9,"images":[]}"""
            : """{"id":9,"user_error":"out of credits"}"""));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ImageRequest(Image()));
        Assert.Contains("out of credits", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Http_errors_preserve_status_and_body(bool discovery)
    {
        var provider = Create(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            { Content = new StringContent("native validation details") });
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            if (discovery) await provider.ListModels();
            else await provider.ImageRequest(Image());
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, error.StatusCode);
        Assert.Contains("native validation details", error.Message);
    }

    [Fact]
    public async Task Image_polling_observes_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = Create(request =>
        {
            if (request.Method == HttpMethod.Get) cancellation.Cancel();
            return Json("""{"id":9,"images":[]}""");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ImageRequest(Image(), cancellation.Token));
    }

    [Theory]
    [InlineData("http://cdn.example.com/image.png")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/relative/image.png")]
    public async Task Non_https_outputs_are_rejected_without_downloading(string url)
    {
        var provider = Create(_ => Json(JsonSerializer.Serialize(new { id = 9, images = new[] { url } })),
            download: _ => throw new Xunit.Sdk.XunitException("Must not download."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ImageRequest(Image()));
    }

    [Fact]
    public async Task Video_lifecycle_is_stateless_and_maps_frames_references_and_native_options()
    {
        Dictionary<string, string[]>? fields = null;
        var provider = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/cli/models") return Json(Catalog);
            Assert.Equal(new AuthenticationHeaderValue("Bearer", "test-api-key"), request.Headers.Authorization);
            Assert.Equal("/tunes/42/prompts", request.RequestUri.AbsolutePath);
            fields = Fields(request);
            return Json("""{"id":99,"images":[]}""");
        });
        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "astria/native-video", Prompt = "Move slowly", Resolution = "1080p", Duration = 5,
            GenerateAudio = false, Seed = 0, N = 2, AspectRatio = "16:9", Fps = 24,
            Image = new VideoFileUrl { Url = "https://inputs.example.com/first.png" },
            FrameImages = [new() { FrameType = "last_frame", Image = new VideoFile { Data = "bGFzdA==", MediaType = "image/png" } }],
            InputReferences = [new VideoFileUrl { Url = "https://inputs.example.com/a.png" }, new VideoFileUrl { Url = "https://inputs.example.com/b.png" }],
            ProviderOptions = Native(new { text = "Still image", video_audio = true, input_video_url = "https://inputs.example.com/reference.mp4" })
        });
        Assert.Equal("42:99", started.Operation);
        Assert.Single(started.Warnings);
        Assert.Equal("native-video", Field(fields!, "video_model"));
        Assert.Equal("Move slowly", Field(fields!, "video_prompt"));
        Assert.Equal("Still image", Field(fields!, "text"));
        Assert.Equal("1080p", Field(fields!, "video_resolution"));
        Assert.Equal("5", Field(fields!, "video_duration"));
        Assert.Equal("false", Field(fields!, "video_audio"));
        Assert.Equal("0", Field(fields!, "seed"));
        Assert.Equal("2", Field(fields!, "num_images"));
        Assert.Equal("16:9", Field(fields!, "aspect_ratio"));
        Assert.Equal("https://inputs.example.com/first.png", Field(fields!, "video_first_frame_url"));
        Assert.Equal("last", Field(fields!, "video_last_frame"));
        Assert.Equal(["https://inputs.example.com/a.png", "https://inputs.example.com/b.png"], fields!["prompt[image_reference_urls][]"]);
        Assert.Equal("https://inputs.example.com/reference.mp4", Field(fields!, "input_video_url"));

        var polls = 0;
        var anotherInstance = Create(request =>
        {
            Assert.Equal("/tunes/42/prompts/99", request.RequestUri!.AbsolutePath);
            Assert.Equal(new AuthenticationHeaderValue("Bearer", "test-api-key"), request.Headers.Authorization);
            return Json(++polls == 1 ? """{"id":99,"images":[],"trained_at":"done","cost_mc":null}"""
                : """{"id":99,"video_model_family":"native-video","images":["https://cdn.example.com/out.mp4"],"cost_mc":200}""");
        }, download: request =>
        {
            Assert.Null(request.Headers.Authorization);
            return Media("video/mp4");
        });
        Assert.IsType<VideoOperationPendingResult>(await anotherInstance.GetVideoOperationStatus(started.Operation));
        var completed = Assert.IsType<VideoOperationCompletedResult>(await anotherInstance.GetVideoOperationStatus(started.Operation));
        Assert.Equal("astria/native-video", completed.Response.ModelId);
        var video = Assert.Single(completed.Videos);
        Assert.Equal("bWVkaWE=", video.Data);
        Assert.Equal("video/mp4", video.MediaType);
        Assert.Equal(200, completed.ProviderMetadata!["astria"].GetProperty("cost_mc").GetInt32());
    }

    [Fact]
    public async Task Video_error_status_preserves_raw_provider_failure()
    {
        var provider = Create(_ => Json("""{"id":99,"user_error":"invalid reference","cost_mc":0}"""));
        var error = Assert.IsType<VideoOperationErrorResult>(await provider.GetVideoOperationStatus("42:99"));
        Assert.Equal("invalid reference", error.Error);
        Assert.Equal("invalid reference", error.ProviderMetadata!["astria"].GetProperty("user_error").GetString());
    }

    [Theory]
    [InlineData("https://evil.example.com/prompts/1")]
    [InlineData("42:../1")]
    [InlineData("0:99")]
    [InlineData("42:99:1")]
    public async Task Video_operation_cannot_target_arbitrary_urls(string operation)
    {
        var provider = Create(_ => throw new Xunit.Sdk.XunitException("Must not call API."));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetVideoOperationStatus(operation));
    }

    [Fact]
    public async Task Video_omitted_settings_do_not_inject_gateway_defaults()
    {
        var provider = Create(request =>
        {
            if (request.Method == HttpMethod.Get) return Json(Catalog);
            var fields = Fields(request);
            Assert.Equal(2, fields.Count);
            Assert.Equal("native-video", Field(fields, "video_model"));
            Assert.Equal("Move", Field(fields, "video_prompt"));
            Assert.DoesNotContain("prompt[text]", fields.Keys);
            return Json("""{"id":99}""");
        });
        await provider.StartVideoOperation(new() { Model = "astria/native-video", Prompt = "Move" });
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("application/octet-stream")]
    [InlineData(null)]
    public async Task Video_intermediate_images_are_pending_until_video_media_is_available(string? contentType)
    {
        var polls = 0;
        var provider = Create(_ => Json(++polls == 1
            ? """{"id":99,"images":["https://cdn.example.com/frame.mp4"],"trained_at":"done"}"""
            : """{"id":99,"images":["https://cdn.example.com/frame.mp4","https://cdn.example.com/extensionless?signature=1"]}"""),
            download: request =>
            {
                Assert.Null(request.Headers.Authorization);
                if (request.RequestUri!.AbsolutePath == "/extensionless")
                    return Media("video/mp4");
                var response = Media("image/png");
                response.Content.Headers.ContentType = contentType is null ? null : new(contentType);
                return response;
            });

        Assert.IsType<VideoOperationPendingResult>(await provider.GetVideoOperationStatus("42:99"));
        var completed = Assert.IsType<VideoOperationCompletedResult>(await provider.GetVideoOperationStatus("42:99"));
        var video = Assert.Single(completed.Videos);
        Assert.Equal("video/mp4", video.MediaType);
        Assert.Equal("bWVkaWE=", video.Data);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Video_only_sends_image_stage_text_when_explicitly_provided(bool hasFirstFrame, bool hasImageStageText)
    {
        var provider = Create(request =>
        {
            if (request.Method == HttpMethod.Get) return Json(Catalog);
            var fields = Fields(request);
            Assert.Equal("Direct video", Field(fields, "video_prompt"));
            if (hasImageStageText)
                Assert.Equal("Render first frame", Field(fields, "text"));
            else
                Assert.DoesNotContain("prompt[text]", fields.Keys);
            if (hasFirstFrame)
                Assert.Equal("https://inputs.example.com/frame.png", Field(fields, "video_first_frame_url"));
            return Json("""{"id":99}""");
        });
        await provider.StartVideoOperation(new()
        {
            Model = "astria/native-video",
            Prompt = "Direct video",
            Image = hasFirstFrame ? new VideoFileUrl { Url = "https://inputs.example.com/frame.png" } : null,
            ProviderOptions = hasImageStageText ? Native(new { text = "Render first frame" }) : null
        });
    }

    [Theory]
    [InlineData("images")]
    [InlineData("video")]
    public async Task Unknown_models_do_not_trigger_paid_calls(string type)
    {
        var provider = Create(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Json(Catalog);
        });
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            if (type == "images") await provider.ImageRequest(Image("astria/unknown"));
            else await provider.StartVideoOperation(new() { Model = "astria/unknown", Prompt = "Move" });
        });
    }

    [Fact]
    public async Task Video_requires_provider_default_without_fabricating_an_image_tune()
    {
        var provider = Create(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Json("""{"video_model_families":{"native-video":{}}}""");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.StartVideoOperation(new() { Model = "astria/native-video", Prompt = "Move" }));
    }

    [Fact]
    public async Task Video_mixed_reference_types_are_rejected_rather_than_reordered()
    {
        var provider = Create(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Json(Catalog);
        });
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.StartVideoOperation(new()
        {
            Model = "astria/native-video", Prompt = "Move",
            InputReferences = [new VideoFileUrl { Url = "https://example.com/a.png" }, new VideoFile { Data = "bWVkaWE=" }]
        }));
    }

    [Fact]
    public async Task Video_status_cancellation_prevents_network_access()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = Create(_ => throw new Xunit.Sdk.XunitException("Must not call API."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetVideoOperationStatus("42:99", cancellation.Token));
    }

    [Fact]
    public async Task Media_downloads_are_unauthenticated_even_on_the_api_host()
    {
        var provider = Create(_ => Json("""{"id":9,"images":["https://api.astria.ai/output.png"]}"""),
            download: request =>
            {
                Assert.Null(request.Headers.Authorization);
                return Media("image/png");
            });
        await provider.ImageRequest(Image());
    }

    [Fact]
    public async Task Explicit_image_input_overrides_conflicting_native_media_fields()
    {
        var provider = Create(request =>
        {
            var fields = Fields(request);
            Assert.Equal("https://inputs.example.com/new.png", Field(fields, "input_image_url"));
            Assert.DoesNotContain("prompt[input_image_base64]", fields.Keys);
            return Json(Completed);
        });
        var request = Image();
        request.Files = [new ImageFileUrl { Url = "https://inputs.example.com/new.png" }];
        request.ProviderOptions = Native(new { input_image_url = "https://inputs.example.com/old.png", input_image_base64 = "b2xk" });
        await provider.ImageRequest(request);
    }

    [Fact]
    public async Task Unified_image_edit_preserves_remote_file_attachments()
    {
        var provider = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/cli/models") return Json(Catalog);
            Assert.Equal("https://inputs.example.com/image.png", Field(Fields(request), "input_image_url"));
            return Json(Completed);
        });
        var request = Chat().ToUnifiedRequest("astria");
        request.Input!.Items!.Last().Content!.Add(new AIFileContentPart
            { Type = "file", MediaType = "image/png", Data = "https://inputs.example.com/image.png" });
        await provider.ExecuteUnifiedAsync(request);
    }

    [Fact]
    public async Task Unified_video_preserves_remote_first_frame_and_reference_video()
    {
        var provider = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/cli/models") return Json(Catalog);
            if (request.Method == HttpMethod.Get)
                return Json("""{"id":99,"images":["https://cdn.example.com/out.mp4"]}""");
            var fields = Fields(request);
            Assert.Equal("https://inputs.example.com/frame.png", Field(fields, "video_first_frame_url"));
            Assert.Equal("https://inputs.example.com/reference.mp4", Field(fields, "input_video_url"));
            Assert.DoesNotContain("prompt[image_reference_urls][]", fields.Keys);
            return Json("""{"id":99}""");
        }, download: _ => Media("video/mp4"));
        var chat = Chat();
        chat.Model = "astria/native-video";
        var request = chat.ToUnifiedRequest("astria");
        request.Input!.Items!.Last().Content!.AddRange([
            new AIFileContentPart { Type = "file", MediaType = "image/png", Data = "https://inputs.example.com/frame.png" },
            new AIFileContentPart { Type = "file", MediaType = "video/mp4", Data = "https://inputs.example.com/reference.mp4" }
        ]);
        await provider.ExecuteUnifiedAsync(request);
    }

    [Fact]
    public async Task OpenAI_generation_and_edit_adapters_emit_only_completed_image_events()
    {
        var provider = Create(_ => Json(Completed));
        var generated = await provider.OpenAIImageGenerationRequestAsync(new() { Model = "astria/42", Prompt = "Draw" });
        Assert.Equal("bWVkaWE=", Assert.Single(generated.Data!).B64Json);
        var generationEvents = new List<IOpenAIImageStreamEvent>();
        await foreach (var item in provider.OpenAIImageGenerationStreamingAsync(new()
            { Model = "astria/42", Prompt = "Draw", Stream = true, PartialImages = 3 }))
            generationEvents.Add(item);
        Assert.IsType<OpenAIImageGenerationCompleted>(Assert.Single(generationEvents));
        var edit = new OpenAIImageEditRequest
        {
            Model = "astria/42", Prompt = "Edit",
            ImageFiles = [new FormFile(new MemoryStream(Encoding.UTF8.GetBytes("input")), 0, 5, "image", "input.png")
                { Headers = new HeaderDictionary(), ContentType = "image/png" }]
        };
        var edited = await provider.OpenAIImageEditRequestAsync(edit);
        Assert.Equal("bWVkaWE=", Assert.Single(edited.Data!).B64Json);
        var editEvents = new List<IOpenAIImageStreamEvent>();
        await foreach (var item in provider.OpenAIImageEditStreamingAsync(edit)) editEvents.Add(item);
        Assert.IsType<OpenAIImageEditCompleted>(Assert.Single(editEvents));
    }

    [Fact]
    public async Task Unified_image_execution_returns_generated_media()
    {
        var provider = Create(request => request.RequestUri!.AbsolutePath == "/cli/models" ? Json(Catalog) : Json(Completed));
        var unified = await provider.ExecuteUnifiedAsync(Chat().ToUnifiedRequest("astria"));
        var tool = Assert.IsType<AIToolCallContentPart>(Assert.Single(Assert.Single(unified.Output!.Items!).Content!));
        Assert.Equal("generate_image", tool.ToolName);
        Assert.True(tool.ProviderExecuted);
        Assert.Contains("bWVkaWE=", JsonSerializer.Serialize(tool.Output));
    }

    [Theory]
    [InlineData("astria/42", "chat")]
    [InlineData("astria/42", "responses")]
    [InlineData("astria/42", "messages")]
    [InlineData("astria/native-video", "chat")]
    [InlineData("astria/native-video", "responses")]
    [InlineData("astria/native-video", "messages")]
    public async Task Unsupported_nonstreaming_conversation_adapters_reject_without_network(string model, string surface)
    {
        var provider = Create(_ => throw new Xunit.Sdk.XunitException("Must not call API."),
            download: _ => throw new Xunit.Sdk.XunitException("Must not download."));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            switch (surface)
            {
                case "chat":
                    var chat = Chat();
                    chat.Model = model;
                    await provider.CompleteChatAsync(chat);
                    break;
                case "responses":
                    await provider.ResponsesAsync(new() { Model = model, Input = "Draw" });
                    break;
                default:
                    await provider.MessagesAsync(new() { Model = model, MaxTokens = 100, Messages = [] }, []);
                    break;
            }
        });
    }

    [Theory]
    [InlineData("responses", "astria/42")]
    [InlineData("messages", "astria/native-video")]
    public async Task Unsupported_streaming_adapters_reject_before_paid_submission(string surface, string model)
    {
        var provider = Create(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/cli/models", request.RequestUri!.AbsolutePath);
            return Json(Catalog);
        }, download: _ => throw new Xunit.Sdk.XunitException("Must not download."));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            if (surface == "responses")
                await foreach (var _ in provider.ResponsesStreamingAsync(new()
                    { Model = model, Input = "Draw" })) { }
            else
                await foreach (var _ in provider.MessagesStreamingAsync(new()
                    { Model = model, MaxTokens = 100, Messages = [] }, [])) { }
        });
    }

    [Fact]
    public async Task Unified_image_stream_contains_synthetic_media_tool_output()
    {
        var provider = Create(request => request.RequestUri!.AbsolutePath == "/cli/models" ? Json(Catalog) : Json(Completed));
        var events = new List<AIStreamEvent>();
        await foreach (var item in provider.StreamUnifiedAsync(Chat().ToUnifiedRequest("astria"))) events.Add(item);
        Assert.Contains(events, item => JsonSerializer.Serialize(item).Contains("tool-output-available"));
        Assert.DoesNotContain(events, item => JsonSerializer.Serialize(item).Contains("text-delta"));
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("messages")]
    [InlineData("ui")]
    public async Task Image_conversation_streaming_wrappers_deliver_media_outputs(string surface)
    {
        var provider = Create(request => request.RequestUri!.AbsolutePath == "/cli/models" ? Json(Catalog) : Json(Completed));
        var chunks = new List<string>();
        switch (surface)
        {
            case "chat":
                await foreach (var part in provider.CompleteChatStreamingAsync(Chat()))
                    chunks.Add(JsonSerializer.Serialize(part));
                break;
            case "messages":
                await foreach (var part in provider.MessagesStreamingAsync(JsonSerializer.Deserialize<MessagesRequest>(
                    """{"model":"astria/42","max_tokens":100,"messages":[{"role":"user","content":"Draw a mountain"}]}""")!, []))
                    chunks.Add(JsonSerializer.Serialize(part, part.GetType()));
                break;
            default:
                await foreach (var part in provider.StreamAsync(new ChatRequest
                {
                    Model = "astria/42",
                    Messages = [new() { Role = Role.user, Parts = [new TextUIPart { Text = "Draw a mountain" }] }]
                }))
                    chunks.Add(JsonSerializer.Serialize(part, part.GetType()));
                break;
        }
        Assert.Contains(chunks, chunk => chunk.Contains("bWVkaWE=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unified_video_execution_returns_generated_media()
    {
        var provider = Create(request => Json(request.RequestUri!.AbsolutePath == "/cli/models"
            ? Catalog
            : request.Method == HttpMethod.Post ? """{"id":99}"""
            : """{"id":99,"video_model_family":"native-video","images":["https://cdn.example.com/video.mp4"]}"""),
            download: _ => Media("video/mp4"));
        var chat = Chat();
        chat.Model = "astria/native-video";
        var unified = await provider.ExecuteUnifiedAsync(chat.ToUnifiedRequest("astria"));
        var tool = Assert.IsType<AIToolCallContentPart>(Assert.Single(Assert.Single(unified.Output!.Items!).Content!));
        Assert.Equal("generate_video", tool.ToolName);
        Assert.True(tool.ProviderExecuted);
        Assert.Contains("bWVkaWE=", JsonSerializer.Serialize(tool.Output));
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("unified")]
    [InlineData("ui")]
    public async Task Video_conversation_streaming_wrappers_deliver_video_outputs(string surface)
    {
        var provider = Create(request => Json(request.RequestUri!.AbsolutePath == "/cli/models"
            ? Catalog
            : request.Method == HttpMethod.Post ? """{"id":99}"""
            : """{"id":99,"video_model_family":"native-video","images":["https://cdn.example.com/video.mp4"]}"""),
            download: _ => Media("video/mp4"));
        var chat = Chat();
        chat.Model = "astria/native-video";
        var chunks = new List<string>();
        switch (surface)
        {
            case "chat":
                await foreach (var part in provider.CompleteChatStreamingAsync(chat))
                    chunks.Add(JsonSerializer.Serialize(part));
                break;
            case "responses":
                await foreach (var part in provider.ResponsesStreamingAsync(JsonSerializer.Deserialize<ResponseRequest>(
                    """{"model":"astria/native-video","input":"Move slowly"}""")!))
                    chunks.Add(JsonSerializer.Serialize(part, part.GetType()));
                break;
            case "unified":
                await foreach (var part in provider.StreamUnifiedAsync(chat.ToUnifiedRequest("astria")))
                    chunks.Add(JsonSerializer.Serialize(part));
                break;
            default:
                await foreach (var part in provider.StreamAsync(new ChatRequest
                {
                    Model = "astria/native-video",
                    Messages = [new() { Role = Role.user, Parts = [new TextUIPart { Text = "Move slowly" }] }]
                }))
                    chunks.Add(JsonSerializer.Serialize(part, part.GetType()));
                break;
        }
        Assert.Contains(chunks, chunk => chunk.Contains("bWVkaWE=", StringComparison.Ordinal));
    }

    private static ChatCompletionOptions Chat() => JsonSerializer.Deserialize<ChatCompletionOptions>(
        """{"model":"astria/42","messages":[{"role":"user","content":"Draw a mountain"}]}""")!;
    private static ImageRequest Image(string model = "astria/42") => new() { Model = model, Prompt = "A mountain" };
    private static Dictionary<string, JsonElement> Native(object value) => new() { ["astria"] = JsonSerializer.SerializeToElement(value) };
    private static string Field(Dictionary<string, string[]> fields, string name) => Assert.Single(fields[$"prompt[{name}]"]);
    private static Dictionary<string, string[]> Fields(HttpRequestMessage request)
        => Assert.IsType<MultipartFormDataContent>(request.Content)
            .GroupBy(x => x.Headers.ContentDisposition!.Name!.Trim('"'))
            .ToDictionary(x => x.Key, x => x.Select(part => part.ReadAsStringAsync().GetAwaiter().GetResult()).ToArray());
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Media(string type)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes("media"));
        content.Headers.ContentType = new(type);
        return new(HttpStatusCode.OK) { Content = content };
    }
    private static AstriaProvider Create(Func<HttpRequestMessage, HttpResponseMessage> respond,
        string? key = "test-api-key", Func<HttpRequestMessage, HttpResponseMessage>? download = null)
        => new(new KeyResolver(key), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(respond, download ?? (_ => Media("image/png"))));
    private sealed class KeyResolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "astria" ? key : null;
    }
    private sealed class ClientFactory(Func<HttpRequestMessage, HttpResponseMessage> api,
        Func<HttpRequestMessage, HttpResponseMessage> download) : IHttpClientFactory
    {
        private int _count;
        public HttpClient CreateClient(string name) => new(new Handler(++_count == 1 ? api : download));
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
