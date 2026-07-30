using System.ComponentModel;
using System.Text.Json;
using AIHappey.Interactions;
using AIHappey.Responses;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    [Description("Ask Google Gemini to analyze a YouTube video.")]
    [McpServerTool(
        Title = "Google YouTube",
        Name = "google_youtube_ask",
        ReadOnly = true,
        Destructive = false,
        UseStructuredContent = true,
        IconSource = "https://www.youtube.com/s/desktop/014dbbed/img/favicon_144x144.png",
        OutputSchemaType = typeof(Interaction),
        Idempotent = false,
        OpenWorld = true)]
    public async Task<CallToolResult> GoogleYouTube_Ask(
        [Description("Question or instruction for the video, such as summarizing it or extracting action points.")] string prompt,
        [Description("YouTube video URL.")] string url,
        [Description("Google Gemini model.")] string model = "gemini-flash-latest",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var videoUri)
            || videoUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("A valid absolute YouTube URL is required.", nameof(url));
        }

        var interaction = await GetInteraction(new InteractionRequest
        {
            Model = model,
            Input = new InteractionsInput(
            [
                new InteractionTextContent { Text = prompt },
                new InteractionVideoContent { Uri = videoUri.AbsoluteUri }
            ]),
            Store = false
        }, cancellationToken);

        return new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(interaction, ResponseJson.Default)
        };
    }

    [Description("Ask Google using Google Maps grounding for places, routes, distances, and location-aware questions.")]
    [McpServerTool(
        Title = "Google Maps",
        Name = "google_maps_ask",
        ReadOnly = true,
        Destructive = false,
        UseStructuredContent = true,
        IconSource = "https://upload.wikimedia.org/wikipedia/commons/thumb/a/aa/Google_Maps_icon_%282020%29.svg/1920px-Google_Maps_icon_%282020%29.svg.png",
        OutputSchemaType = typeof(Interaction),
        Idempotent = false,
        OpenWorld = true)]
    public async Task<CallToolResult> GoogleMaps_Ask(
        [Description("Location-aware question or task.")] string prompt,
        [Description("Google Gemini model.")] string model = "gemini-flash-latest",
        [Description("Optional latitude used to bias local results.")] double? latitude = null,
        [Description("Optional longitude used to bias local results.")] double? longitude = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        if (latitude is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(latitude));
        if (longitude is < -180 or > 180)
            throw new ArgumentOutOfRangeException(nameof(longitude));
        if (latitude.HasValue != longitude.HasValue)
            throw new ArgumentException("Latitude and longitude must be supplied together.");

        var interaction = await GetInteraction(new InteractionRequest
        {
            Model = model,
            Input = new InteractionsInput(prompt),
            Store = false,
            Tools =
            [
                new InteractionGoogleMapsTool
                {
                    Latitude = latitude,
                    Longitude = longitude
                }
            ]
        }, cancellationToken);

        return new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(interaction, ResponseJson.Default)
        };
    }
}
