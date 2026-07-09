using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.MCP.Telemetry;
using AIHappey.Responses;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Core.MCP.WebSearch;

[McpServerToolType]
public class WebSearchTools
{
    private const string WebSearchPromptTemplate = "You are an expert AI with live web access. Answer the following user question by searching the web for the most accurate and up-to-date information. Cite your main sources or URLs if available. Be concise but include relevant details.\n\nQuestion:\n###\n{0}\n###\n";
    private const string AcademicSearchPromptTemplate = "You are an academic research assistant with access to peer-reviewed journals, scholarly databases, and academic search engines. Answer the following user question by searching for the most credible, up-to-date academic sources. Provide a concise summary, highlight key findings, and always cite your primary sources (e.g., DOI, journal name, author, year, or a direct URL to the publication).\n\nQuestion:\n###\n{0}\n###\n";

    private static readonly string[] ModelNames = [
        "perplexity/sonar-pro",
        "openai/gpt-5.4-mini",
        "google/gemini-3.5-flash",
        "anthropic/claude-haiku-4-5-20251001",
        "spacexai/grok-4.20-0309-non-reasoning",
        "mistral/mistral-medium-latest",
     //   "groq/openai/gpt-oss-20b"
      ];

    private static readonly string[] AcademicModelNames = ["perplexity/sonar-reasoning-pro",
        "openai/gpt-5.2",
        "google/gemini-pro-latest",
        "anthropic/claude-opus-4-7", "spacexai/grok-4.3", "mistral/mistral-large-latest"];

    [Description("Perform a quick web search using Google AI with Google Search grounding.")]
    [McpServerTool(
        Title = "Google web search",
        Name = "web_search_google",
        ReadOnly = true)]
    public static async Task<CallToolResult?> WebSearch_Google(
        [Description("Search query")] string query,
        IServiceProvider serviceProvider,
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken = default) =>
        await requestContext.WithExceptionCheck(async () =>
        {
            var modelName = "google/gemini-3.5-flash";
            var startTime = DateTime.UtcNow;

            var result = await ExecuteResponseAsync(
                serviceProvider,
                modelName,
                FormatPrompt(WebSearchPromptTemplate, query),
                maxOutputTokens: null,
                metadata: CreateGoogleMetadata(),
                startedAt: startTime,
                cancellationToken);

            AddDuration(result, startTime);

            return new CallToolResult
            {
                StructuredContent = JsonSerializer.SerializeToElement(result, ResponseJson.Default)
            };
        });

    [Description("Parallel web search across multiple AI models, optionally filtered by date range. If a date range is used, include it in the prompt, as some providers don’t support date filters.")]
    [McpServerTool(Title = "Web search (multi-model)",
        Name = "web_search_execute",
        OpenWorld = true,
        Destructive = false,
        Idempotent = false,
        ReadOnly = true)]
    public static async Task<CallToolResult?> WebSearch_Execute(
        [Description("Search query")] string query,
        IServiceProvider serviceProvider,
        RequestContext<CallToolRequestParams> requestContext,
        [Description("Start date of the date range")] string? startDate = null,
        [Description("End date of the date range")] string? endDate = null,
        [Description("Search context size. low, medium or high")] string? searchContextSize = "medium",
        CancellationToken cancellationToken = default) =>
        await requestContext.WithExceptionCheck(async () =>
        {
            var prompt = FormatPrompt(WebSearchPromptTemplate, query);
            var tasks = ModelNames.Select(modelName => ExecuteModelSafelyAsync(
                serviceProvider,
                requestContext,
                modelName,
                prompt,
                maxOutputTokens: 10000,
                metadata: CreateWebSearchMetadata(startDate, endDate, searchContextSize),
                cancellationToken));

            var results = await Task.WhenAll(tasks);
            var successfulResults = results.OfType<ResponseResult>().ToArray();

            return new CallToolResult
            {
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    Results = successfulResults
                }, ResponseJson.Default),
                Meta = CreateGatewayMeta(successfulResults)
            };
        });

    [Description("Academic web search using multiple AI models in parallel")]
    [McpServerTool(Title = "Academic web search (multi-model)",
        Name = "web_search_academic",
        Destructive = false,
        OpenWorld = true,
        Idempotent = false,
        ReadOnly = true)]
    public static async Task<CallToolResult?> WebSearch_ExecuteAcademic(
        [Description("Search query")] string query,
        IServiceProvider serviceProvider,
        RequestContext<CallToolRequestParams> requestContext,
        [Description("Start date of the date range")] string? startDate = null,
        [Description("End date of the date range")] string? endDate = null,
        [Description("Search context size. low, medium or high")] string? searchContextSize = "medium",
        CancellationToken cancellationToken = default) =>
        await requestContext.WithExceptionCheck(async () =>
        {
            var prompt = FormatPrompt(AcademicSearchPromptTemplate, query);
            var tasks = AcademicModelNames.Select(modelName => ExecuteModelSafelyAsync(
                serviceProvider,
                requestContext,
                modelName,
                prompt,
                maxOutputTokens: 10000,
                metadata: CreateAcademicSearchMetadata(startDate, endDate, searchContextSize),
                cancellationToken));

            var results = await Task.WhenAll(tasks);
            var successfulResults = results.OfType<ResponseResult>().ToArray();

            return new CallToolResult
            {
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    Results = successfulResults
                }, ResponseJson.Default),
                Meta = CreateGatewayMeta(successfulResults)
            };
        });

    private static async Task<ResponseResult?> ExecuteModelSafelyAsync(
        IServiceProvider serviceProvider,
        RequestContext<CallToolRequestParams> requestContext,
        string modelName,
        string prompt,
        int? maxOutputTokens,
        Dictionary<string, object?> metadata,
        CancellationToken cancellationToken)
    {
        try
        {
            var startTime = DateTime.UtcNow;
            var result = await ExecuteResponseAsync(
                serviceProvider,
                modelName,
                prompt,
                maxOutputTokens,
                metadata,
                startedAt: startTime,
                cancellationToken);

            AddDuration(result, startTime);
            return result;
        }
        catch (Exception ex)
        {
            await requestContext.Server.SendNotificationAsync(
                $"{modelName} failed: {ex.Message}",
                LoggingLevel.Error,
                cancellationToken: CancellationToken.None);

            return null;
        }
    }

    private static async Task<ResponseResult> ExecuteResponseAsync(
        IServiceProvider serviceProvider,
        string modelName,
        string prompt,
        int? maxOutputTokens,
        Dictionary<string, object?> metadata,
        DateTime startedAt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("'query' is required.");

        var resolver = serviceProvider.GetRequiredService<IAIModelProviderResolver>();
        var provider = await resolver.Resolve(modelName, cancellationToken);

        var request = new ResponseRequest
        {
            Model = modelName.SplitModelId().Model,
            Input = new ResponseInput(prompt),
            MaxOutputTokens = maxOutputTokens,
            Store = false,
            Stream = false,
            Metadata = metadata
        };

        var result = await provider.ResponsesAsync(request, cancellationToken);
        await serviceProvider.TrackMcpResponsesTelemetryAsync(result, provider, request.Temperature ?? 1, startedAt, cancellationToken);

        return result;
    }

    private static Dictionary<string, object?> CreateGoogleMetadata() => new()
    {
        ["google"] = new
        {
            tools = CreateGoogleTools(),
            thinkingConfig = new
            {
                thinkingBudget = -1
            }
        }
    };

    private static Dictionary<string, object?> CreateWebSearchMetadata(
        string? startDate,
        string? endDate,
        string? searchContextSize) => new()
        {
            ["perplexity"] = new
            {
                search_mode = "web",
                web_search_options = new
                {
                    search_context_size = searchContextSize
                },
                last_updated_before_filter = endDate,
                last_updated_after_filter = startDate
            },
            ["google"] = new
            {
                tools = CreateGoogleTools(),
                generation_config = new
                {
                    thinking_level = "minimal"
                }
            },
            ["openai"] = new
            {
                tools = new object[]
                {
                    new { type = "web_search" }
                },
                reasoning = new
                {
                    effort = "low"
                }
            },
            ["mistral"] = new
            {
                tools = new object[]
                {
                    new { type = "web_search_premium" }
                }
            },
            ["groq"] = new
            {
                tools = new object[]
                {
                    new { type = "browser_search" }
                }
            },
            ["xai"] = new
            {
                tools = new object[]
                {
                    new { type = "web_search" },
                    new { type = "x_search" }
                }
            },
            ["anthropic"] = new
            {
                tools = new object[]
                {
                    new
                    {
                        type = "web_search_20260209",
                        name = "web_search",
                        allowed_callers = new[] { "direct" },
                        max_uses = searchContextSize == "low" ? 2 : searchContextSize == "high" ? 6 : 7
                    },
                    new
                    {
                        type = "web_fetch_20260309",
                        name = "web_fetch",
                        allowed_callers = new[] { "direct" },
                        max_uses = searchContextSize == "low" ? 2 : searchContextSize == "high" ? 6 : 4
                    }
                },
                thinking = new
                {
                    budget_tokens = 1024,
                    type = "enabled"
                }
            }
        };

    private static Dictionary<string, object?> CreateAcademicSearchMetadata(
        string? startDate,
        string? endDate,
        string? searchContextSize) => new()
        {
            ["perplexity"] = new
            {
                search_mode = "academic",
                web_search_options = new
                {
                    search_context_size = searchContextSize
                }
            },
            ["google"] = new
            {
                tools = CreateGoogleTools(),
                thinkingConfig = new
                {
                    thinkingBudget = -1
                }
            },
            ["xai"] = new
            {
                tools = new object[]
                {
                    new { type = "web_search" }
                },
                reasoning = new { }
            },
            ["mistral"] = new
            {
                tools = new object[]
                {
                    new { type = "web_search_premium" }
                }
            },
            ["openai"] = new
            {
                tools = new object[]
                {
                    new { type = "web_search" }
                },
                reasoning = new
                {
                    effort = "low"
                }
            },
            ["anthropic"] = new
            {
                tools = new object[]
                {
                    new
                    {
                        type = "web_search_20260209",
                        name = "web_search",
                        allowed_callers = new[] { "direct" },
                        max_uses = searchContextSize == "low" ? 3 : searchContextSize == "high" ? 7 : 5
                    },
                    new
                    {
                        type = "web_fetch_20260309",
                        name = "web_fetch",
                        allowed_callers = new[] { "direct" },
                        max_uses = searchContextSize == "low" ? 3 : searchContextSize == "high" ? 7 : 5
                    }
                },
                thinking = new
                {
                    type = "adaptive"
                }
            }
        };

    private static object[] CreateGoogleTools()
    {
        List<object> tools =
        [
            new
            {
                type = "google_search"
            }
        ];

        return [.. tools];
    }

    private static string FormatPrompt(string promptTemplate, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("'query' is required.");

        return string.Format(promptTemplate, query);
    }

    private static void AddDuration(ResponseResult result, DateTime startTime)
    {
        result.Metadata ??= [];
        result.Metadata["duration"] = (DateTime.UtcNow - startTime).ToString();
    }

    private static JsonObject CreateGatewayMeta(IEnumerable<ResponseResult> results)
    {
        decimal totalGatewayCost = 0m;

        foreach (var result in results)
        {
            if (TryGetGatewayCost(result, out var gatewayCost))
                totalGatewayCost += gatewayCost;
        }

        return new JsonObject
        {
            ["gateway"] = new JsonObject
            {
                ["cost"] = totalGatewayCost
            }
        };
    }

    private static bool TryGetGatewayCost(ResponseResult result, out decimal cost)
    {
        cost = 0m;

        if (result.Metadata is null
            || !result.Metadata.TryGetValue("gateway", out var gateway)
            || gateway is null)
        {
            return false;
        }

        return TryGetGatewayCostValue(gateway, out cost);
    }

    private static bool TryGetGatewayCostValue(object gateway, out decimal cost)
    {
        cost = 0m;

        if (gateway is Dictionary<string, object?> gatewayDict
            && gatewayDict.TryGetValue("cost", out var gatewayCost))
        {
            return TryGetDecimalValue(gatewayCost, out cost);
        }

        var gatewayElement = gateway switch
        {
            JsonElement json => json,
            _ => JsonSerializer.SerializeToElement(gateway, JsonSerializerOptions.Web)
        };

        if (gatewayElement.ValueKind != JsonValueKind.Object
            || !TryGetPropertyCaseInsensitive(gatewayElement, "cost", out var gatewayCostElement))
        {
            return false;
        }

        return TryGetDecimalValue(gatewayCostElement, out cost);
    }

    private static bool TryGetDecimalValue(object? rawValue, out decimal value)
    {
        value = 0m;

        return rawValue switch
        {
            decimal decimalValue => (value = decimalValue) >= 0m || decimalValue < 0m,
            int intValue => (value = intValue) >= 0m || intValue < 0,
            long longValue => (value = longValue) >= 0m || longValue < 0,
            float floatValue => (value = (decimal)floatValue) >= 0m || floatValue < 0,
            double doubleValue => (value = (decimal)doubleValue) >= 0m || doubleValue < 0,
            string stringValue when decimal.TryParse(stringValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                => (value = parsed) >= 0m || parsed < 0m,
            JsonElement jsonElement => TryGetDecimalFromJson(jsonElement, out value),
            _ => false
        };
    }

    private static bool TryGetDecimalFromJson(JsonElement valueElement, out decimal value)
    {
        value = 0m;

        return valueElement.ValueKind switch
        {
            JsonValueKind.Number when valueElement.TryGetDecimal(out var parsedNumber)
                => (value = parsedNumber) >= 0m || parsedNumber < 0m,
            JsonValueKind.String when decimal.TryParse(valueElement.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedString)
                => (value = parsedString) >= 0m || parsedString < 0m,
            _ => false
        };
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement json, string propertyName, out JsonElement value)
    {
        if (json.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in json.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
