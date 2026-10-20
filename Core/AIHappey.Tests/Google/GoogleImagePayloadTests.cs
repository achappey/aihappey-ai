using System.Reflection;
using System.Text.Json;
using AIHappey.Core.Providers.Google;
using AIHappey.Interactions;

namespace AIHappey.Tests.Google;

public sealed class GoogleImagePayloadTests
{
    [Theory]
    [InlineData(null, "image/jpeg")]
    [InlineData("jpeg", "image/jpeg")]
    [InlineData("jpg", "image/jpeg")]
    [InlineData("png", "image/png")]
    [InlineData("image/png", "image/png")]
    [InlineData("image/webp", "image/webp")]
    public void ImageResponseFormatUsesRequestedMimeType(string? requestedMimeType, string expectedMimeType)
    {
        var method = typeof(GoogleAIProvider).GetMethod(
            "CreateGoogleImageInteractionRequest",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(GoogleAIProvider), "CreateGoogleImageInteractionRequest");

        var request = (InteractionRequest)method.Invoke(null,
        [
            "google/gemini-3-pro-image",
            new List<InteractionContent> { new InteractionTextContent { Text = "image prompt" } },
            null,
            null,
            requestedMimeType
        ])!;
        var responseFormat = JsonSerializer.SerializeToElement(request.ResponseFormat);

        Assert.Equal(expectedMimeType, responseFormat.GetProperty("mime_type").GetString());
    }
}
