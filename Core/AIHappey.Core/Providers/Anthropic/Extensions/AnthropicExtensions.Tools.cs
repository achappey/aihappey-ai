using System.Text.Json;
using AIHappey.Common.Model.Providers;
using Anthropic.SDK.Common;
using Anthropic.SDK.Messaging;
using ANT = Anthropic.SDK;

namespace AIHappey.Core.Providers.Anthropic.Extensions;

public static partial class AnthropicExtensions
{
    public static bool ContainsLocation(this ANT.Messaging.UserLocation userLocation)
        => !string.IsNullOrEmpty(userLocation.City);

    public static ANT.Common.Tool ToTool(this AIHappey.Common.Model.Tool tool)
        => new(new Function(
                       tool?.Name,
                       tool?.Description,
                       JsonSerializer.Serialize(tool?.InputSchema, JsonSerializerOptions.Web)));

    public static List<ANT.Common.Tool> ToTools(this IEnumerable<AIHappey.Common.Model.Tool>? tools)
        => tools?.Select(a => a.ToTool()).ToList() ?? [];

    public static ANT.Common.Tool ToWebSearchTool(this Common.Model.Providers.WebSearch webSearch)
         => ServerTools.GetWebSearchTool(maxUses: webSearch?.MaxUses ?? 5,
                allowedDomains: webSearch?.AllowedDomains,
                blockedDomains: webSearch?.BlockedDomains,
                userLocation: webSearch?.UserLocation?.ContainsLocation() == true ? webSearch?.UserLocation : null);

    public static ANT.Common.Tool GetCodeExecutionTool(this Common.Model.Providers.CodeExecution codeExecution)
         => new Function("code_execution", "code_execution_20250825", []);


    public static ANT.Common.Tool ToWebFetchTool(this WebFetch webFetch)
    {
        Dictionary<string, object> dictionary = [];
        if (webFetch.MaxUses.HasValue == true)
        {
            dictionary.Add("max_uses", webFetch.MaxUses);
        }

        if (webFetch.AllowedDomains != null
            && webFetch.AllowedDomains.Count > 0)
        {
            dictionary.Add("allowed_domains", webFetch.AllowedDomains);
        }

        if (webFetch.BlockedDomains != null
            && webFetch.BlockedDomains.Count > 0)
        {
            dictionary.Add("blocked_domains", webFetch.BlockedDomains);
        }

        return new Function("web_fetch", "web_fetch_20250910", dictionary);
    }

    public static List<ANT.Common.Tool> WithDefaultTools(this List<ANT.Common.Tool> tools,
        Common.Model.Providers.AnthropicProviderMetadata? a)
    {
        if (a?.WebSearch is { } ws) tools.Add(ws.ToWebSearchTool());
        if (a?.CodeExecution is { } ce) tools.Add(ce.GetCodeExecutionTool());
        if (a?.WebFetch is { } wf) tools.Add(wf.ToWebFetchTool());
        if (a?.Memory is not null) tools.Add(new Function("memory", "memory_20250818", []));

        return tools;
    }


}
