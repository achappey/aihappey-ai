using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Common.MCP;

public static class McpCommonExtensions
{
    public static IServiceCollection AddMcpServers(this IServiceCollection services,
        IEnumerable<McpServerDefinition> definitions)
    {
        services
            .AddMcpServer()
            .WithHttpTransport(http =>
            {
                http.ConfigureSessionOptions = async (ctx, opts, cancellationToken) =>
                {
                    var serverUrlSegment = ctx.Request.Path.Value?
                        .Trim('/')
                        .Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault();

                    var serverDef = definitions.FirstOrDefault(d => string.Equals(d.Name, serverUrlSegment, StringComparison.InvariantCultureIgnoreCase));

                    if (serverUrlSegment != null && serverDef != null)
                    {
                        opts.ServerInfo = new Implementation()
                        {
                            Version = "1.0.0",
                            Name = GetReversedHostFromPath(ctx.Request) + "/" + serverDef.Name,
                            Title = serverDef.Title ?? serverDef.Name.Replace("-", " "),
                            Description = serverDef.Description
                        };

                        // Build Prompts
                        if (serverDef.PromptTypes != null)
                        {
                            var prompts = BuildPrompts(ctx.RequestServices, serverDef.PromptTypes);

                            opts.Handlers.ListPromptsHandler = async (_, _ct) =>
                            {
                                var visible = prompts
                                    .Select(p => new Prompt
                                    {
                                        Name = p.ProtocolPrompt.Name,
                                        Title = p.ProtocolPrompt.Title,
                                        Description = p.ProtocolPrompt.Description,
                                        Arguments = p.ProtocolPrompt.Arguments,
                                        Meta = p.ProtocolPrompt.Meta
                                    })
                                    .ToArray();

                                return await Task.FromResult(new ListPromptsResult { Prompts = visible });
                            };

                            opts.Handlers.GetPromptHandler = async (req, _ct) =>
                            {
                                var name = req.Params?.Name ?? "";
                                var p = prompts.FirstOrDefault(x => x.ProtocolPrompt.Name == name)
                                    ?? throw new McpException($"Prompt '{name}' not available in '{serverDef.Name}'.");
                                return await p.GetAsync(req, _ct);
                            };
                        }

                        // Build Tools
                        if (serverDef.ToolTypes != null)
                        {
                            var tools = BuildTools(ctx.RequestServices, serverDef.ToolTypes);

                            opts.Handlers.ListToolsHandler = async (_, _ct) =>
                            {
                                var visible = tools
                                    .Select(tl => new Tool
                                    {
                                        Name = tl.ProtocolTool.Name,
                                        Title = tl.ProtocolTool.Title,
                                        Description = tl.ProtocolTool.Description,
                                        InputSchema = tl.ProtocolTool.InputSchema,
                                        OutputSchema = tl.ProtocolTool.OutputSchema,
                                        Annotations = tl.ProtocolTool.Annotations,
                                        Meta = tl.ProtocolTool.Meta
                                    })
                                    .ToArray();

                                return await Task.FromResult(new ListToolsResult { Tools = visible });
                            };

                            opts.Handlers.CallToolHandler = async (req, _ct) =>
                            {
                                var name = req.Params?.Name ?? "";
                                var t = tools.FirstOrDefault(x => x.ProtocolTool.Name == name)
                                    ?? throw new McpException($"Tool '{name}' not available in '{serverDef.Name}'.");

                                return await t.InvokeAsync(req, _ct);
                            };
                        }
                    }

                    await Task.CompletedTask;
                };
            });

        return services;
    }

    public static void MapMcpEndpoints(this WebApplication app,
        IEnumerable<McpServerDefinition> definitions, bool requireAuth)
    {
        foreach (var def in definitions)
        {
            if (requireAuth)
                app.MapMcp($"/{def.Name}").RequireAuthorization();
            else
                app.MapMcp($"/{def.Name}");
        }
    }

    public static void MapMcpRegistry(this WebApplication app, IEnumerable<McpServerDefinition> definitions, dynamic[]? icons = null)
    {
        app.MapGet("/v0.1/servers", (HttpContext context) =>
        {
            var host = context.Request.Host.ToString();
            var scheme = context.Request.Scheme;
            var rev = host.ToReverseDns();

            object ServerEntry(McpServerDefinition def) => new
            {
                server = new
                {
                    schema = "https://static.modelcontextprotocol.io/schemas/2025-10-17/server.schema.json",
                    name = $"{rev}/{def.Name}",
                    title = $"{def.Name}",
                    icons,
                    description = def.Description?.Replace("{host}", host) ?? $"Mcp server {def.Name} on {host}.",
                    remotes = new[]
                    {
                        new
                        {
                            type = "streamable-http",
                            url = $"{scheme}://{host}/{def.Name.ToLowerInvariant()}"
                        }
                    }
                }
            };

            List<object> servers = [];

            foreach (var def in definitions)
            {
                servers.Add(ServerEntry(def));
            }

            return Results.Json(new { servers });
        })
        .AllowAnonymous();
    }

    // Helpers
    private static string? GetReversedHostFromPath(HttpRequest ctx)
    {
        var host = ctx.Path.Value?
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(host))
            return null;

        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        Array.Reverse(parts);
        return string.Join('.', parts);
    }

    private static string ToReverseDns(this string host)
    {
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        Array.Reverse(parts);
        return string.Join(".", parts);
    }

    private static List<McpServerPrompt> BuildPrompts(IServiceProvider sp, Type[] types)
    {
        var list = new List<McpServerPrompt>();
        foreach (var t in types)
        {
            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            foreach (var mi in methods)
            {
                if (mi.GetCustomAttribute<McpServerPromptAttribute>() is null) continue;

                McpServerPrompt p = mi.IsStatic
                    ? McpServerPrompt.Create(mi, target: null, new McpServerPromptCreateOptions { Services = sp })
                    : McpServerPrompt.Create(mi,
                        r => ActivatorUtilities.CreateInstance(r.Services!, t),
                        new McpServerPromptCreateOptions { Services = sp });

                list.Add(p);
            }
        }
        return list;
    }

    private static List<McpServerTool> BuildTools(IServiceProvider sp, Type[] types)
    {
        var list = new List<McpServerTool>();
        foreach (var t in types)
        {
            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            foreach (var mi in methods)
            {
                if (mi.GetCustomAttribute<McpServerToolAttribute>() is null) continue;

                McpServerTool tool = mi.IsStatic
                    ? McpServerTool.Create(mi, target: null, new McpServerToolCreateOptions { Services = sp })
                    : McpServerTool.Create(mi,
                        r => ActivatorUtilities.CreateInstance(r.Services!, t),
                        new McpServerToolCreateOptions { Services = sp });

                list.Add(tool);
            }
        }
        return list;
    }
}
