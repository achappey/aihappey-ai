using System.Globalization;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Astria;

public sealed partial class AstriaProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        var files = request.Files?.ToList() ?? [];
        if (files.Count > 1)
            throw new NotSupportedException("Astria accepts one input image; use native reference options for additional references.");
        var tune = await ResolveTuneAsync(request.Model, cancellationToken);
        var options = Options(request.ProviderOptions);
        options["text"] = request.Prompt;
        if (request.N.HasValue) options["num_images"] = request.N.Value;
        if (request.Seed.HasValue) options["seed"] = request.Seed.Value;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) options["aspect_ratio"] = request.AspectRatio;
        if (!string.IsNullOrWhiteSpace(request.Size))
        {
            var parts = request.Size.Split('x', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height)
                || width <= 0 || height <= 0)
                throw new ArgumentException("Astria image size must be WIDTHxHEIGHT.");
            options["w"] = width;
            options["h"] = height;
        }
        if (files.Count > 0) RemoveMediaOptions(options, "input_image");
        if (request.Mask is not null) RemoveMediaOptions(options, "mask_image");
        using var form = Form(options);
        foreach (var file in files)
            AddMedia(form, "input_image", (file as ImageFileUrl)?.Url, file.Data, file.MediaType);
        if (request.Mask is { } mask)
            AddMedia(form, "mask_image", (mask as ImageFileUrl)?.Url, mask.Data, mask.MediaType);

        var root = await SubmitAsync(tune, form, cancellationToken);
        var promptId = PromptId(root);
        if (!Terminal(root))
            root = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
                ct => PollAsync(tune, promptId, ct), Terminal,
                TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(10), null, cancellationToken);
        if (!string.IsNullOrWhiteSpace(Text(root, "user_error")))
            throw new InvalidOperationException($"Astria prompt {PromptId(root)} failed: {Text(root, "user_error")}");
        var images = new List<string>();
        foreach (var url in Outputs(root))
        {
            var (data, type) = await DownloadAsync(url, "image/png", cancellationToken);
            images.Add($"data:{type};base64,{data}");
        }
        return new ImageResponse
        {
            Images = images,
            ProviderMetadata = Metadata(root),
            Response = new() { ModelId = tune.ToModelId(GetIdentifier()), Timestamp = DateTime.UtcNow }
        };
    }
}
