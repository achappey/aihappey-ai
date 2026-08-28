using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Radient;

public partial class RadientProvider
{
    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.File);
        ApplyAuthHeader();

        using var form = new MultipartFormDataContent();
        await using var source = options.File.OpenReadStream();
        var file = new StreamContent(source);
        if (!string.IsNullOrWhiteSpace(options.File.ContentType))
            file.Headers.ContentType = new MediaTypeHeaderValue(options.File.ContentType);
        form.Add(file, "file", options.File.FileName);
        AddForm(form, "model", StripProviderPrefix(options.Model));
        AddForm(form, "prompt", options.Prompt);
        AddForm(form, "response_format", options.ResponseFormat ?? "json");
        AddForm(form, "temperature", options.Temperature?.ToString(CultureInfo.InvariantCulture));
        AddForm(form, "language", options.Language);
        if (TryGetMetadataValue(options.AdditionalProperties, "provider", out var provider)) AddForm(form, "provider", provider);

        using var response = await _client.PostAsync("v1/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Radient transcription failed ({(int)response.StatusCode}): {raw}");

        var text = raw;
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
            || raw.TrimStart().StartsWith('{'))
        {
            using var document = JsonDocument.Parse(raw);
            text = document.RootElement.TryGetProperty("text", out var value) ? value.GetString() ?? "" : "";
            if (document.RootElement.TryGetProperty("status", out var status)
                && string.Equals(status.GetString(), "failed", StringComparison.OrdinalIgnoreCase))
            {
                var error = document.RootElement.TryGetProperty("error", out var errorValue) ? errorValue.GetString() : null;
                throw new InvalidOperationException($"Radient transcription failed: {error ?? "unknown error"}");
            }
        }

        return new OpenAITranscriptionResponse { Text = text };
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private static void AddForm(MultipartFormDataContent form, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) form.Add(new StringContent(value), name);
    }

    private static bool TryGetMetadataValue(Dictionary<string, JsonElement>? values, string name, out string? value)
    {
        value = null;
        if (values is null) return false;
        if (values.TryGetValue(name, out var direct) && direct.ValueKind == JsonValueKind.String)
        {
            value = direct.GetString();
            return true;
        }
        if (values.TryGetValue("radient", out var nested) && nested.ValueKind == JsonValueKind.Object
            && nested.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return true;
        }
        return false;
    }
}
