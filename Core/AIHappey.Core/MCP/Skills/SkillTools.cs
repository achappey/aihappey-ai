using System.ComponentModel;
using System.Text.Json;
using AIHappey.Common.Model.Skills;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.MCP.Telemetry;
using AIHappey.Responses;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Core.MCP.Skills;

[McpServerToolType]
public sealed class SkillTools
{
    private const int DefaultLimit = 10;
    private const int MaximumLimit = 50;

    [Description("Search the available Agent Skills catalog using concise keywords.")]
    [McpServerTool(Title = "Search skills", Name = "ai_search_skills", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    public static async Task<CallToolResult?> SearchSkills(
        IServiceProvider services,
        RequestContext<CallToolRequestParams> requestContext,
        [Description("Concise keyword search terms. If omitted, returns the first catalog entries.")] string? query = null,
        [Description("Maximum number of results. Defaults to 10 and is capped at 50.")] int? limit = null,
        CancellationToken cancellationToken = default)
        => await requestContext.WithExceptionCheck(async () =>
        {
            var skills = await LoadSkillsAsync(services, cancellationToken);
            var normalizedQuery = query?.Trim() ?? string.Empty;
            var matches = normalizedQuery.Length == 0
                ? skills
                : skills.Where(skill => Contains(skill.Id, normalizedQuery)
                    || Contains(skill.Name, normalizedQuery)
                    || Contains(skill.Description, normalizedQuery))
                    .ToArray();

            return CreateSearchResult("keyword", normalizedQuery, matches, ClampLimit(limit));
        });

    [Description("Use AI to discover the Agent Skills most relevant to a natural-language task or prompt.")]
    [McpServerTool(Title = "Discover skills with AI", Name = "ai_discover_skills", ReadOnly = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    public static async Task<CallToolResult?> DiscoverSkills(
        [Description("Natural-language task or prompt describing the needed capability.")] string prompt,
        [Description("Gateway model ID to use for discovery, for example openai/gpt-5.6-luna.")] string model,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> requestContext,
        [Description("Maximum number of results. Defaults to 10 and is capped at 50.")] int? limit = null,
        CancellationToken cancellationToken = default)
        => await requestContext.WithExceptionCheck(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            var skills = await LoadSkillsAsync(services, cancellationToken);
            var maxResults = ClampLimit(limit);
            if (skills.Length == 0)
                return CreateSearchResult("ai", prompt.Trim(), [], maxResults);

            var catalog = JsonSerializer.Serialize(skills.Select(skill => new
            {
                id = skill.Id,
                name = skill.Name,
                description = skill.Description
            }), JsonSerializerOptions.Web);
            var instruction = "Select the Agent Skills relevant to the user's task. Return exactly one JSON object shaped {\"skill_ids\":[\"exact-id\"]}. Use only exact ids from the catalog, order by relevance, include no duplicates, return at most " + maxResults + " ids, and output no markdown or other text.";
            var input = JsonSerializer.Serialize(new { task = prompt.Trim(), skills = JsonSerializer.Deserialize<JsonElement>(catalog) }, JsonSerializerOptions.Web);
            var resolver = services.GetRequiredService<IAIModelProviderResolver>();
            var provider = await resolver.Resolve(model, cancellationToken);
            var request = new ResponseRequest
            {
                Model = model.SplitModelId().Model,
                Instructions = instruction,
                Input = new ResponseInput(input),
                Store = false,
                Stream = false
            };
            var startedAt = DateTime.UtcNow;
            var response = await provider.ResponsesAsync(request, cancellationToken);
            await services.TrackMcpResponsesTelemetryAsync(response, provider, request.Temperature ?? 1, startedAt, cancellationToken);

            var selected = SelectDiscoveredSkills(response.Output.GetAssistantOutputText(), skills, maxResults);
            return CreateSearchResult("ai", prompt.Trim(), selected, maxResults);
        });

    [Description("Load the instructions and resource list for an available Agent Skill.")]
    [McpServerTool(Title = "Activate skill", Name = "ai_activate_skill", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    public static async Task<CallToolResult?> ActivateSkill(
        [Description("Exact skill id returned by search_skills or discover_skills.")] string skill_id,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> requestContext,
        [Description("Optional exact skill version. If omitted, the latest/default content is loaded.")] string? version = null,
        CancellationToken cancellationToken = default)
        => await requestContext.WithExceptionCheck(async () =>
        {
            var resolver = services.GetRequiredService<IAISkillProviderResolver>();
            var bundle = await SkillBundle.LoadAsync(resolver, skill_id, version, cancellationToken);
            var resourcePaths = bundle.Resources.Keys.Order(StringComparer.Ordinal).ToArray();
            var resourcesXml = resourcePaths.Length == 0
                ? "<skill_resources />"
                : "<skill_resources>\n" + string.Join("\n", resourcePaths.Select(path => $"  <file>{EscapeXml(path)}</file>")) + "\n</skill_resources>";

            return new CallToolResult
            {
                IsError = false,
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    skill = new
                    {
                        id = bundle.SkillId,
                        skill_id = bundle.SkillId,
                        version = bundle.Version,
                        name = bundle.Name,
                        description = bundle.Description,
                        resourcePaths,
                        instructions = bundle.Body
                    }
                }, JsonSerializerOptions.Web),
                Content = [new TextContentBlock
                {
                    Text = $"<skill_content skill_id=\"{EscapeXml(bundle.SkillId)}\" name=\"{EscapeXml(bundle.Name)}\">\n{bundle.Body}\n\nUse read_skill_resource with this skill_id, the same optional version, and a relative path from the resource list when needed.\n{resourcesXml}\n</skill_content>"
                }]
            };
        });

    [Description("Read a bundled file from an available Agent Skill by relative path.")]
    [McpServerTool(Title = "Read skill resource", Name = "ai_read_skill_resource", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    public static async Task<CallToolResult?> ReadSkillResource(
        [Description("Exact skill id that owns the resource.")] string skill_id,
        [Description("Relative path inside the skill bundle, for example references/REFERENCE.md or scripts/run.py.")] string path,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> requestContext,
        [Description("Optional exact skill version. Use the same version passed to activate_skill.")] string? version = null,
        CancellationToken cancellationToken = default)
        => await requestContext.WithExceptionCheck(async () =>
        {
            var resolver = services.GetRequiredService<IAISkillProviderResolver>();
            var bundle = await SkillBundle.LoadAsync(resolver, skill_id, version, cancellationToken);
            var relativePath = SkillBundle.NormalizeRelativePath(path);
            if (!bundle.Resources.TryGetValue(relativePath, out var resource))
                throw new FileNotFoundException($"Resource '{relativePath}' was not found in skill '{skill_id}'.");

            var uri = $"skill://{Uri.EscapeDataString(bundle.SkillId)}/{string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString))}";
            if (resource.IsText)
            {
                var text = resource.ReadText();
                return new CallToolResult
                {
                    IsError = false,
                    StructuredContent = JsonSerializer.SerializeToElement(new { skillResource = new { skill_id = bundle.SkillId, version = bundle.Version, skillName = bundle.Name, path = relativePath, mimeType = resource.MimeType, text } }, JsonSerializerOptions.Web),
                    Content = [new EmbeddedResourceBlock { Resource = new TextResourceContents { Uri = uri, MimeType = resource.MimeType, Text = text } }]
                };
            }

            return new CallToolResult
            {
                IsError = false,
                StructuredContent = JsonSerializer.SerializeToElement(new { skillResource = new { skill_id = bundle.SkillId, version = bundle.Version, skillName = bundle.Name, path = relativePath, mimeType = resource.MimeType, encoding = "base64", data = Convert.ToBase64String(resource.Bytes) } }, JsonSerializerOptions.Web),
                Content = [new EmbeddedResourceBlock { Resource = new BlobResourceContents { Uri = uri, MimeType = resource.MimeType, Blob = resource.Bytes } }]
            };
        });

    private static async Task<Skill[]> LoadSkillsAsync(IServiceProvider services, CancellationToken cancellationToken)
        => [.. (await services.GetRequiredService<IAISkillProviderResolver>().ResolveSkills(order: "asc", ct: cancellationToken)).Data ?? []];

    private static CallToolResult CreateSearchResult(string mode, string query, IReadOnlyCollection<Skill> matches, int limit)
    {
        var selected = matches.Take(limit).ToArray();
        var lines = selected.Select(skill => $"- id={skill.Id}; skill_id={skill.Id}; name={skill.Name}: {skill.Description}");
        return new CallToolResult
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(new { skillSearch = new { mode, query, totalMatches = matches.Count, returned = selected.Length, skills = selected } }, JsonSerializerOptions.Web),
            Content = [new TextContentBlock { Text = $"<skill_search mode=\"{mode}\" query=\"{EscapeXml(query)}\" total_matches=\"{matches.Count}\" returned=\"{selected.Length}\">\n{(selected.Length == 0 ? "No matching skills found." : string.Join("\n", lines))}\nUse activate_skill with a returned skill_id before following its instructions.\n</skill_search>" }]
        };
    }

    private static Skill[] SelectDiscoveredSkills(string text, IReadOnlyCollection<Skill> catalog, int limit)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("skill_ids", out var ids) || ids.ValueKind != JsonValueKind.Array)
                return [];
            var byId = catalog.ToDictionary(skill => skill.Id, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return [.. ids.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString())
                .Where(id => id is not null && seen.Add(id) && byId.ContainsKey(id))
                .Take(limit)
                .Select(id => byId[id!])];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool Contains(string? value, string query)
        => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static int ClampLimit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaximumLimit);

    private static string EscapeXml(string? value) => (value ?? string.Empty)
        .Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

}
