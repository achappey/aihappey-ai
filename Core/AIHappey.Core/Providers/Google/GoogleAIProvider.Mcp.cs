using System.ComponentModel;
using System.Text.Json;
using AIHappey.Interactions;
using AIHappey.Responses;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
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
        [Description("Google Gemini model.")] string model = "gemini-2.5-flash",
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
