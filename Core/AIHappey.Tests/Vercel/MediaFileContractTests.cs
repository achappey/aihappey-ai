using System.Text.Json;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Vercel;

public sealed class MediaFileContractTests
{
    [Fact]
    public void UrlOnlyImageFilesAndMaskPassMvcValidationAndRoundTrip()
    {
        var request = JsonSerializer.Deserialize<ImageRequest>("""
        {"model":"openai/gpt-image-2","prompt":"edit","files":[{"url":"https://example.com/image.png","type":"url"}],
         "mask":{"type":"url","url":"https://example.com/mask.png"}}
        """, JsonSerializerOptions.Web)!;

        Assert.IsType<ImageFileUrl>(Assert.Single(request.Files!));
        Assert.IsType<ImageFileUrl>(request.Mask);
        Assert.True(ValidateMvc(request).IsValid);
        var json = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);
        Assert.Equal(2, json.GetProperty("files")[0].EnumerateObject().Count());
        Assert.False(json.GetProperty("mask").TryGetProperty("data", out _));
        Assert.IsType<ImageFileUrl>(Assert.Single(JsonSerializer.Deserialize<ImageRequest>(json, JsonSerializerOptions.Web)!.Files!));
        Assert.Equal("{\"type\":\"url\",\"url\":\"https://example.com/image.png\"}",
            JsonSerializer.Serialize(new ImageFileUrl { Url = "https://example.com/image.png" }, JsonSerializerOptions.Web));
    }

    [Fact]
    public void AllVideoInputFieldsAcceptMixedFileAndUrlVariants()
    {
        var request = JsonSerializer.Deserialize<VideoRequest>("""
        {"model":"test/video","prompt":"animate",
         "image":{"type":"url","url":"https://example.com/source.png"},
         "inputReferences":[{"type":"file","mediaType":"image/png","data":"AQID"},{"type":"url","url":"https://example.com/ref.png"}],
         "frameImages":[{"frameType":"first_frame","image":{"type":"url","url":"https://example.com/first.png"}}]}
        """, JsonSerializerOptions.Web)!;

        Assert.True(ValidateMvc(request).IsValid);
        Assert.IsType<VideoFileUrl>(request.Image);
        Assert.IsType<VideoFile>(request.InputReferences!.First());
        Assert.IsType<VideoFileUrl>(request.InputReferences!.Last());
        var roundTrip = JsonSerializer.Deserialize<VideoRequest>(JsonSerializer.Serialize(request, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
        Assert.IsType<VideoFileUrl>(Assert.Single(roundTrip.FrameImages!).Image);
        Assert.True(ValidateMvc(roundTrip).IsValid);
    }

    [Theory]
    [InlineData("{\"type\":\"file\",\"data\":\"AQID\"}")]
    [InlineData("{\"type\":\"file\",\"mediaType\":\"image/png\"}")]
    [InlineData("{\"type\":\"url\"}")]
    [InlineData("{\"type\":\"url\",\"url\":\"\"}")]
    [InlineData("{\"type\":\"unknown\",\"data\":\"AQID\"}")]
    public void InvalidVariantsAreRejectedDuringDeserialization(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ImageFile>(json, JsonSerializerOptions.Web));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<VideoFile>(json, JsonSerializerOptions.Web));
    }

    [Fact]
    public void InlineFilesStillRequireDataAndMediaTypeInMvcValidation()
    {
        var imageState = ValidateMvc(new ImageRequest { Model = "test/image", Prompt = "edit", Files = [new ImageFile()] });
        Assert.False(imageState.IsValid);
        Assert.Contains("Files[0].Data", imageState.Keys);
        Assert.Contains("Files[0].MediaType", imageState.Keys);
        var videoState = ValidateMvc(new VideoRequest { Model = "test/video", Prompt = "edit", Image = new VideoFile() });
        Assert.False(videoState.IsValid);
        Assert.Contains("Image.Data", videoState.Keys);
        Assert.Contains("Image.MediaType", videoState.Keys);
    }

    [Fact]
    public void LegacyInlineWithoutDiscriminatorAndMixedImageInputsRoundTrip()
    {
        var request = JsonSerializer.Deserialize<ImageRequest>("""
        {"model":"test/image","prompt":"edit","files":[{"mediaType":"image/png","data":"AQID"},{"type":"url","url":"https://example.com/ref.png"}]}
        """, JsonSerializerOptions.Web)!;
        Assert.True(ValidateMvc(request).IsValid);
        var roundTrip = JsonSerializer.Deserialize<ImageRequest>(JsonSerializer.Serialize(request, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
        Assert.Equal("AQID", roundTrip.Files!.First().Data);
        Assert.IsType<ImageFileUrl>(roundTrip.Files!.Last());
    }

    private static ModelStateDictionary ValidateMvc(object model)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        using var provider = services.BuildServiceProvider();
        var context = new ActionContext(new DefaultHttpContext { RequestServices = provider },
            new RouteData(), new ActionDescriptor(), new ModelStateDictionary());
        provider.GetRequiredService<IObjectModelValidator>().Validate(context, null, "", model);
        return context.ModelState;
    }
}
