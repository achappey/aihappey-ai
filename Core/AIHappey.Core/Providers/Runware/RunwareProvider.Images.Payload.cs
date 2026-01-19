using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Runware;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Runware;

public sealed partial class RunwareProvider
{
    private static Dictionary<string, object?> BuildImageInferencePayload(
        ImageRequest request,
        RunwareImageProviderMetadata? options)
    {
        var payload = new Dictionary<string, object?>
        {
            ["taskType"] = "imageInference",
            ["taskUUID"] = Guid.NewGuid().ToString(),
            ["model"] = request.Model,
            ["positivePrompt"] = request.Prompt,
        };

        AddIfNotNull(payload, "deliveryMethod", options?.DeliveryMethod);
        AddIfNotNull(payload, "outputType", options?.OutputType);
        AddIfNotNull(payload, "outputFormat", options?.OutputFormat);
        AddIfNotNull(payload, "outputQuality", options?.OutputQuality);
        AddIfNotNull(payload, "ttl", options?.Ttl);
        AddIfNotNull(payload, "includeCost", options?.IncludeCost);

        AddIfNotNull(payload, "steps", options?.Steps);
        AddIfNotNull(payload, "clipSkip", options?.ClipSkip);
        AddIfNotNull(payload, "CFGScale", options?.CFGScale);
        AddIfNotNull(payload, "negativePrompt", string.IsNullOrWhiteSpace(options?.NegativePrompt) ? null : options!.NegativePrompt);

        AddIfNotNull(payload, "safety", options?.Safety);

        if (request.N is not null)
            payload["numberResults"] = request.N.Value;

        if (request.Seed is not null)
            payload["seed"] = (long)request.Seed.Value;
        else
            AddIfNotNull(payload, "seed", options?.Seed);

        AddIfNotNull(payload, "strength", options?.Strength);

        if (TryParseSize(request.Size ?? string.Empty) is { } wh)
        {
            payload["width"] = wh.width;
            payload["height"] = wh.height;
        }

        AddImageInputs(payload, request, options);  

        if (HasJsonValue(options?.ControlNet))
            payload["controlnet"] = options!.ControlNet!.Value;

        if (HasJsonValue(options?.IpAdapter))
            payload["ipAdapter"] = options!.IpAdapter!.Value;

        // BFL models expect providerSettings.bfl (but we don't add a Bfl property to RunwareProviderSettings).
        // Translate providerOptions.runware.bfl into the raw Runware payload.
        if (request.Model.StartsWith("bfl:", StringComparison.OrdinalIgnoreCase))
        {
            var bfl = options?.Bfl;
            if (request.Model.Equals("bfl:2@2", StringComparison.OrdinalIgnoreCase) is false && bfl is not null)
                bfl.Raw = null;

            if (bfl?.HasAnyValue() == true)
            {
                payload["providerSettings"] = new Dictionary<string, object?>
                {
                    ["bfl"] = bfl
                };
            }
        }

        if (request.Model.StartsWith("openai:"))
        {
            payload["providerSettings"] = new RunwareProviderSettings()
            {
                OpenAI = options?.ProviderSettings?.OpenAI ?? new RunwareOpenAiProviderSettings()
            };
        }

        if (request.Model.StartsWith("midjourney:"))
        {
            payload["providerSettings"] = new RunwareProviderSettings()
            {
                Midjourney = options?.ProviderSettings?.Midjourney ?? new RunwareMidjourneyProviderSettings()
            };
        }

        if (request.Model.StartsWith("runway:"))
        {
            payload["providerSettings"] = new RunwareProviderSettings()
            {
                Runway = options?.ProviderSettings?.Runway ?? new RunwareRunwayProviderSettings()
            };
        }

        if (request.Model.StartsWith("google:"))
        {
            payload["providerSettings"] = new RunwareProviderSettings()
            {
                Google = options?.ProviderSettings?.Google ?? new RunwareGoogleProviderSettings()
            };
        }

        if (request.Model.StartsWith("alibaba:", StringComparison.OrdinalIgnoreCase))
        {
            payload["providerSettings"] = new RunwareProviderSettings()
            {
                Alibaba = options?.ProviderSettings?.Alibaba ?? new RunwareAlibabaProviderSettings()
            };
        }

        if (request.Model.StartsWith("prunaai:", StringComparison.OrdinalIgnoreCase))
        {
            payload["providerSettings"] = new RunwareProviderSettings()
            {
                PrunaAi = options?.ProviderSettings?.PrunaAi ?? new RunwarePrunaAiProviderSettings()
            };
        }

        if (request.Model.StartsWith("ideogram:", StringComparison.OrdinalIgnoreCase))
        {
            payload["providerSettings"] = new RunwareProviderSettings()
            {
                Ideogram = options?.ProviderSettings?.Ideogram ?? new RunwareIdeogramProviderSettings()
            };
        }

        if (request.Model.StartsWith("bytedance:", StringComparison.OrdinalIgnoreCase))
        {
            payload["providerSettings"] = new RunwareProviderSettings()
            {
                Bytedance = options?.ProviderSettings?.Bytedance ?? new RunwareBytedanceProviderSettings()
            };
        }

        if (request.Model.StartsWith("bria:", StringComparison.OrdinalIgnoreCase))
        {
            // Bria models expect providerSettings.bria.
            var bria = options?.ProviderSettings?.Bria ?? new RunwareBriaProviderSettings();

            if (payload.TryGetValue("providerSettings", out var psObj))
            {
                if (psObj is RunwareProviderSettings ps)
                {
                    ps.Bria = bria;
                }
                else if (psObj is Dictionary<string, object?> dict)
                {
                    dict["bria"] = bria;
                }
                else
                {
                    payload["providerSettings"] = new RunwareProviderSettings { Bria = bria };
                }
            }
            else
            {
                payload["providerSettings"] = new RunwareProviderSettings { Bria = bria };
            }
        }

        return payload;
    }

    private static bool HasJsonValue(JsonElement? el)
        => el is { } v && v.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);

    private static void AddIfNotNull(Dictionary<string, object?> payload, string key, object? value)
    {
        if (value is not null)
            payload[key] = value;
    }
}


