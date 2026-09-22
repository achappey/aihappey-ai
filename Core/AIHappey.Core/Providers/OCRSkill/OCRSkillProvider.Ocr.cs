using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.OCRSkill;

public sealed partial class OCRSkillProvider
{
    private const string OcrModelId = "ocr";
    private const int MaximumFileSize = 20 * 1024 * 1024;

    private static readonly HashSet<string> SupportedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif", "image/bmp", "image/tiff",
        "application/pdf", "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.ms-powerpoint", "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/vnd.oasis.opendocument.text", "application/vnd.oasis.opendocument.spreadsheet",
        "application/vnd.oasis.opendocument.presentation", "application/rtf", "text/rtf", "text/csv"
    };

    private static readonly IReadOnlyDictionary<string, OcrFieldType> SupportedFields =
        CreateSupportedFields();

    private async Task<AIResponse> ExecuteOcrUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateModel(request.Model);

        var files = GetLatestUserFiles(request);
        var outputMode = ResolveOutputMode(request);
        var output = new List<AIOutputItem>(files.Count);

        ApplyAuthHeader();
        for (var index = 0; index < files.Count; index++)
        {
            var file = NormalizeFile(files[index], index);
            var endpoint = outputMode.Structured
                ? BuildStructuredEndpoint(outputMode.Fields)
                : "ocr";

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(file.Bytes)
            };
            httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(file.MediaType);
            httpRequest.Content.Headers.ContentLength = file.Bytes.Length;

            using var response = await _client.SendAsync(httpRequest, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"OCRSkill OCR failed for '{file.Filename}' ({(int)response.StatusCode}): {body}",
                    null,
                    response.StatusCode);
            }

            JsonElement? structuredContent = null;
            var text = body;
            if (outputMode.Structured)
            {
                try
                {
                    using var document = JsonDocument.Parse(body);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        throw new JsonException("The response root is not a JSON object.");
                    structuredContent = document.RootElement.Clone();
                    text = JsonSerializer.Serialize(structuredContent.Value, JsonSerializerOptions.Web);
                }
                catch (JsonException exception)
                {
                    throw new InvalidOperationException(
                        $"OCRSkill returned invalid structured JSON for '{file.Filename}'.",
                        exception);
                }
            }

            var metadata = new Dictionary<string, object?>
            {
                ["filename"] = file.Filename,
                ["mediaType"] = file.MediaType,
                ["fileIndex"] = index,
                ["outputFormat"] = outputMode.Structured ? "json" : "markdown"
            };
            if (structuredContent is not null)
                metadata["ocrskill.structured_output"] = structuredContent.Value;

            output.Add(new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Metadata = metadata,
                Content = [new AITextContentPart { Type = "text", Text = text }]
            });
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = $"{GetIdentifier()}/{OcrModelId}",
            Status = "completed",
            Output = new AIOutput { Items = output },
            Metadata = new Dictionary<string, object?>
            {
                ["finishReason"] = "stop",
                ["fileCount"] = files.Count,
                ["outputFormat"] = outputMode.Structured ? "json" : "markdown",
                ["fields"] = outputMode.Fields
            }
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamOcrUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecuteOcrUnifiedAsync(request, cancellationToken);

        foreach (var item in response.Output?.Items ?? [])
        {
            foreach (var text in (item.Content ?? []).OfType<AITextContentPart>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = Guid.NewGuid().ToString("n");
                var timestamp = DateTimeOffset.UtcNow;
                yield return CreateStreamEvent(id, "text-start", new AITextStartEventData(), timestamp, item.Metadata);
                if (!string.IsNullOrEmpty(text.Text))
                    yield return CreateStreamEvent(id, "text-delta", new AITextDeltaEventData { Delta = text.Text }, timestamp, item.Metadata);
                yield return CreateStreamEvent(id, "text-end", new AITextEndEventData(), timestamp, item.Metadata);
            }
        }

        var completedAt = DateTimeOffset.UtcNow;
        yield return CreateStreamEvent(
            request.Id ?? Guid.NewGuid().ToString("n"),
            "finish",
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = response.Model,
                CompletedAt = completedAt.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    response.Model ?? $"{GetIdentifier()}/{OcrModelId}",
                    completedAt,
                    response.Usage)
            },
            completedAt,
            response.Metadata,
            response.Output);
    }

    private AIStreamEvent CreateStreamEvent(
        string id,
        string type,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata,
        AIOutput? output = null)
        => new()
        {
            ProviderId = GetIdentifier(),
            Metadata = metadata,
            Event = new AIEventEnvelope
            {
                Id = id,
                Type = type,
                Timestamp = timestamp,
                Data = data,
                Metadata = metadata,
                Output = output
            }
        };

    private static void ValidateModel(string? model)
    {
        var normalized = model?.Trim() ?? string.Empty;
        if (normalized.StartsWith("ocrskill/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["ocrskill/".Length..];

        if (!string.Equals(normalized, OcrModelId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unsupported OCRSkill model '{model}'. Use 'ocrskill/{OcrModelId}'.", nameof(model));
    }

    private static List<AIFileContentPart> GetLatestUserFiles(AIRequest request)
    {
        var files = request.Input?.Items?
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))?
            .Content?.OfType<AIFileContentPart>().ToList() ?? [];
        if (files.Count == 0)
            throw new ArgumentException("OCRSkill OCR requires at least one file in the latest user message.", nameof(request));
        return files;
    }

    private static NormalizedOcrFile NormalizeFile(AIFileContentPart file, int index)
    {
        var value = file.Data switch
        {
            string text => text.Trim(),
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString()?.Trim(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"OCRSkill OCR file {index + 1} is empty.", nameof(file));
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"OCRSkill OCR file {index + 1} cannot be a remote URL.", nameof(file));

        var mediaType = file.MediaType?.Trim();
        var base64 = value;
        if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = base64.IndexOf(',');
            if (comma < 0 || !base64[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"OCRSkill OCR file {index + 1} must use a base64 data URL.", nameof(file));
            var header = base64[5..comma];
            var separator = header.IndexOf(';');
            var dataMediaType = separator > 0 ? header[..separator] : header;
            if (!string.IsNullOrWhiteSpace(mediaType)
                && !string.Equals(mediaType, dataMediaType, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"OCRSkill OCR file {index + 1} has conflicting media types.", nameof(file));
            mediaType = dataMediaType;
            base64 = base64[(comma + 1)..];
        }

        if (string.IsNullOrWhiteSpace(mediaType) || !SupportedMediaTypes.Contains(mediaType))
            throw new ArgumentException($"OCRSkill OCR file {index + 1} has unsupported media type '{mediaType ?? "<missing>"}'.", nameof(file));

        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException exception)
        {
            throw new ArgumentException($"OCRSkill OCR file {index + 1} contains invalid base64 data.", nameof(file), exception);
        }
        if (bytes.Length > MaximumFileSize)
            throw new ArgumentException($"OCRSkill OCR file {index + 1} exceeds the 20 MB maximum file size.", nameof(file));

        var filename = string.IsNullOrWhiteSpace(file.Filename) ? $"document-{index + 1}" : file.Filename!;
        return new NormalizedOcrFile(filename, mediaType, bytes);
    }

    private static OcrOutputMode ResolveOutputMode(AIRequest request)
    {
        if (request.ResponseFormat is not null)
            return ResolveResponseFormat(request.ResponseFormat, request);

        var metadata = request.Metadata.GetProviderMetadata<JsonElement>("ocrskill");
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return new OcrOutputMode(false, null);
        if (metadata.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("OCRSkill provider metadata must be an object.", nameof(request));

        var properties = metadata.EnumerateObject().ToArray();
        if (properties.Any(property => !string.Equals(property.Name, "fields", StringComparison.Ordinal)))
            throw new ArgumentException("OCRSkill provider metadata supports only the 'fields' property.", nameof(request));
        if (!metadata.TryGetProperty("fields", out var fieldsElement))
            return new OcrOutputMode(false, null);
        if (fieldsElement.ValueKind != JsonValueKind.String)
            throw new ArgumentException("OCRSkill provider metadata 'fields' must be a comma-separated string.", nameof(request));

        var fields = ValidateFields(fieldsElement.GetString());
        return new OcrOutputMode(true, string.Join(',', fields));
    }

    private static OcrOutputMode ResolveResponseFormat(object responseFormat, AIRequest request)
    {
        JsonElement format;
        try { format = JsonSerializer.SerializeToElement(responseFormat, JsonSerializerOptions.Web); }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new ArgumentException("OCRSkill response_format must be a JSON object.", nameof(request), exception);
        }
        if (format.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("OCRSkill response_format must be a JSON object.", nameof(request));

        var type = format.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;
        if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
            return new OcrOutputMode(false, null);
        if (string.Equals(type, "json_object", StringComparison.OrdinalIgnoreCase))
            return new OcrOutputMode(true, null);
        if (!string.Equals(type, "json_schema", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"OCRSkill response_format type '{type ?? "<missing>"}' is not supported. Use 'text', 'json_object', or 'json_schema'.", nameof(request));

        if (!format.TryGetProperty("json_schema", out var jsonSchema) || jsonSchema.ValueKind != JsonValueKind.Object
            || !jsonSchema.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("OCRSkill json_schema response_format requires json_schema.schema to be an object.", nameof(request));
        if (!schema.TryGetProperty("type", out var schemaType) || schemaType.GetString() != "object")
            throw new ArgumentException("OCRSkill json_schema root type must be 'object'.", nameof(request));
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("OCRSkill json_schema requires top-level object properties.", nameof(request));

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var requiredElement))
        {
            if (requiredElement.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("OCRSkill json_schema required must be an array.", nameof(request));
            foreach (var item in requiredElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()) || !required.Add(item.GetString()!))
                    throw new ArgumentException("OCRSkill json_schema required contains an invalid or duplicate field name.", nameof(request));
            }
        }

        var fields = new List<string>();
        foreach (var property in properties.EnumerateObject())
        {
            if (!SupportedFields.TryGetValue(property.Name, out var documentedType))
                throw new ArgumentException($"OCRSkill does not support structured field '{property.Name}'.", nameof(request));
            ValidateSchemaPropertyType(property.Name, property.Value, documentedType, request);
            fields.Add(required.Remove(property.Name) ? property.Name : $"{property.Name}?");
        }
        if (fields.Count == 0)
            throw new ArgumentException("OCRSkill json_schema must request at least one supported property.", nameof(request));
        if (required.Count > 0)
            throw new ArgumentException($"OCRSkill json_schema required references properties that are not defined: {string.Join(", ", required)}.", nameof(request));

        return new OcrOutputMode(true, string.Join(',', fields));
    }

    private static void ValidateSchemaPropertyType(string name, JsonElement property, OcrFieldType documentedType, AIRequest request)
    {
        if (property.ValueKind != JsonValueKind.Object
            || !property.TryGetProperty("type", out var typeElement))
            throw new ArgumentException($"OCRSkill structured field '{name}' requires an explicit JSON schema type.", nameof(request));

        var types = typeElement.ValueKind switch
        {
            JsonValueKind.String => new[] { typeElement.GetString()! },
            JsonValueKind.Array => typeElement.EnumerateArray()
                .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
                .Where(value => value is not null)
                .Cast<string>()
                .ToArray(),
            _ => []
        };
        var expected = documentedType == OcrFieldType.Number ? "number" : "string";
        if (!types.Contains(expected, StringComparer.Ordinal) || types.Any(type => type is not ("string" or "number" or "null")))
            throw new ArgumentException($"OCRSkill structured field '{name}' must use JSON schema type '{expected}'.", nameof(request));

        if (documentedType == OcrFieldType.Date
            && property.TryGetProperty("format", out var format)
            && (format.ValueKind != JsonValueKind.String || !string.Equals(format.GetString(), "date", StringComparison.Ordinal)))
            throw new ArgumentException($"OCRSkill date field '{name}' supports only JSON schema format 'date'.", nameof(request));
    }

    private static IReadOnlyList<string> ValidateFields(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("OCRSkill provider metadata 'fields' cannot be empty.", nameof(value));
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawField in value.Split(','))
        {
            var field = rawField.Trim();
            if (field.Length == 0)
                throw new ArgumentException("OCRSkill provider metadata 'fields' contains an empty field name.", nameof(value));
            var name = field.EndsWith("?", StringComparison.Ordinal) ? field[..^1] : field;
            if (name.Length == 0 || !SupportedFields.ContainsKey(name))
                throw new ArgumentException($"OCRSkill does not support structured field '{name}'.", nameof(value));
            if (!seen.Add(name))
                throw new ArgumentException($"OCRSkill provider metadata 'fields' contains duplicate field '{name}'.", nameof(value));
            result.Add(field.EndsWith("?", StringComparison.Ordinal) ? $"{name}?" : name);
        }
        return result;
    }

    private static string BuildStructuredEndpoint(string? fields)
        => string.IsNullOrWhiteSpace(fields)
            ? "ocr.json"
            : $"ocr.json?fields={Uri.EscapeDataString(fields)}";

    private static IReadOnlyDictionary<string, OcrFieldType> CreateSupportedFields()
    {
        var result = new Dictionary<string, OcrFieldType>(StringComparer.Ordinal);
        Add(OcrFieldType.String,
            "last_name", "first_name", "sex_gender", "street_name", "street_number", "address_rest",
            "town_or_city", "county", "judet", "region", "country", "nationality", "cnp",
            "id_card_series_number", "id_card_issuer", "id_card_birth_place", "certificate_id", "license_plate",
            "holder_last_name", "holder_company", "holder_first_name", "holder_full_address", "owner_last_name",
            "owner_company", "owner_first_name", "owner_full_address", "vehicle_brand", "vehicle_model",
            "vehicle_commercial_summary", "vehicle_identification_number", "max_technical_weight", "vehicle_mass",
            "vehicle_category", "type_approval_number", "engine_capacity", "engine_power", "fuel_type",
            "power_to_weight_ratio", "vehicle_color", "seats", "standing_places", "vehicle_identification_series",
            "certificate_issuer", "company_name", "buyer_name", "seller_name", "title", "sub_title",
            "article_abstract", "article_body", "article_tags", "tags", "concise_summary", "event_name",
            "event_venue", "event_dates", "thumbnail_hook_text", "video_title", "video_channel",
            "video_length_str", "date_posted_str");
        Add(OcrFieldType.Date,
            "birthdate", "expiration_date", "issue_date", "first_registration_date", "registration_valid_until",
            "registration_date", "certificate_issue_date", "last_itp_date", "invoice_date", "receipt_date",
            "publish_date", "event_date");
        Add(OcrFieldType.Number, "total_amount");
        return result;

        void Add(OcrFieldType type, params string[] names)
        {
            foreach (var name in names)
                result.Add(name, type);
        }
    }

    private enum OcrFieldType { String, Date, Number }

    private sealed record OcrOutputMode(bool Structured, string? Fields);

    private sealed record NormalizedOcrFile(string Filename, string MediaType, byte[] Bytes);
}
