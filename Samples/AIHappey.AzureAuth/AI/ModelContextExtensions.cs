using System.Net;
using System.Reflection;
using AIHappey.AzureAuth.MCP.Models;
using AIHappey.AzureAuth.MCP.ProviderMetadata;
using AIHappey.AzureAuth.MCP.Requests;
using AIHappey.AzureAuth.MCP.Tools;
using AIHappey.AzureAuth.MCP.Users;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.AzureAuth.AI;

public static class ModelContextExtensions
{
   public static void AddMcpMappings(this WebApplication app)
   {
      foreach (var server in Servers)
      {
         app.MapMcp($"/{server}").RequireAuthorization();
      }
   }

   private static readonly List<string> Servers =
   [
      "AI-Users",
      "AI-Models",
      "AI-Tools",
      "AI-Requests",
      "AI-Providers",
   ];

   // Map: logical server -> types that define prompts/tools for that server
   static readonly Dictionary<string, Type[]> PromptTypes = new(StringComparer.OrdinalIgnoreCase)
   {
      ["AI-Users"] = [typeof(UserPrompts)],
      ["AI-Models"] = [typeof(ModelPrompts)],
      ["AI-Tools"] = [typeof(ToolPrompts)],
      ["AI-Requests"] = [typeof(RequestPrompts)],
   };

   static readonly Dictionary<string, Type[]> ToolTypes = new(StringComparer.OrdinalIgnoreCase)
   {
      ["AI-Users"] = [typeof(UserTools)],
      ["AI-Models"] = [typeof(ModelTools)],
      ["AI-Tools"] = [typeof(ToolTools)],
      ["AI-Requests"] = [typeof(RequestTools)],
      ["AI-Providers"] = [typeof(ProviderTools)]
   };

 public static string? GetReversedHostFromPath(HttpRequest ctx)
{
    // extract the first segment
    var host = ctx.Path.Value?
        .Trim('/')
        .Split('/', StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault();

    if (string.IsNullOrWhiteSpace(host))
        return null;

    // reverse labels: a.b.c → c.b.a
    var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
    Array.Reverse(parts);
    return string.Join('.', parts);
}


   public static IServiceCollection AddMcpServers(this IServiceCollection services)
   {
      services
          .AddMcpServer()
          .WithHttpTransport(http =>
          {
             http.ConfigureSessionOptions = async (ctx, opts, cancellationToken) =>
             {
                var server = ctx.Request.Path.Value?
                 .Trim('/')
                 .Split('/', StringSplitOptions.RemoveEmptyEntries)
                 .FirstOrDefault();

                var serverName = Servers.FirstOrDefault(a => a.Equals(server, StringComparison.InvariantCultureIgnoreCase));

                if (server != null && serverName != null)
                {
                   opts.ServerInfo = new Implementation()
                   {
                      Version = "1.0.0",
                      Name = GetReversedHostFromPath(ctx.Request) + "/" + serverName,
                      Title = serverName?.Replace("-", " ")
                   };

                   // 2) Helpers to enumerate prompts/tools for THIS server, built on the fly
                   static List<McpServerPrompt> BuildPrompts(IServiceProvider sp, Type[] types)
                   {
                      var list = new List<McpServerPrompt>();
                      foreach (var t in types)
                      {
                         var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                         foreach (var mi in methods)
                         {
                            if (mi.GetCustomAttribute<McpServerPromptAttribute>() is null) continue;

                            // static → target null; instance → create per request via DI
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

                   static List<McpServerTool> BuildTools(IServiceProvider sp, Type[] types)
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

                   // 3) Build per-request views
                   if (PromptTypes.TryGetValue(server, out Type[]? value))
                   {
                      var prompts = BuildPrompts(ctx.RequestServices, value);

                      // 4) List prompts (scoped)
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

                      // 5) Get prompt (create + render on demand)
                      opts.Handlers.GetPromptHandler = async (req, _ct) =>
                      {
                         var name = req.Params?.Name ?? "";
                         var p = prompts.FirstOrDefault(x => x.ProtocolPrompt.Name == name)
                          ?? throw new McpException($"Prompt '{name}' not available in '{server}'.");
                         return await p.GetAsync(req, _ct);
                      };
                   }

                   if (ToolTypes.TryGetValue(server, out Type[]? toolValues))
                   {
                      var tools = BuildTools(ctx.RequestServices, toolValues);

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
                          ?? throw new McpException($"Tool '{name}' not available in '{server}'.");

                         return await t.InvokeAsync(req, _ct);
                      };
                   }
                }

                await Task.CompletedTask;
             };
          });

      return services;
   }

   private static string ToReverseDns(this string host)
   {
      var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
      Array.Reverse(parts);
      return string.Join(".", parts);
   }


   public static void AddMcpRegistry(this WebApplication app, dynamic[] icons)
   {
      app.MapGet("/v0.1/servers", (HttpContext context) =>
      {
         var host = context.Request.Host.ToString();
         var scheme = context.Request.Scheme;
         var rev = host.ToReverseDns();
         object ServerEntry(string resource) => new
         {
            server = new
            {
               schema = "https://static.modelcontextprotocol.io/schemas/2025-10-17/server.schema.json",
               name = $"{rev}/{resource}",
               title = $"{resource}",
               icons,
               description = Descriptions.TryGetValue(resource, out var desc)
                        ? desc.Replace("{host}", host)
                        : $"Statistics and usage data for {resource} on {host}.",
               remotes = new[]
            {
                new
                {
                    type = "streamable-http",
                    url = $"{scheme}://{host}/{resource.ToLowerInvariant()}"
                }
              }
            }
         };

         List<object> servers = [];

         foreach (var server in Servers)
         {
            servers.Add(ServerEntry(server));
         }

         return Results.Json(new { servers });
      })
      .AllowAnonymous();
   }

   static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase)
   {
      ["AI-Users"] = "Shows how people use AI and how often on {host}.",
      ["AI-Models"] = "Overview of all AI model usage on {host}.",
      ["AI-Tools"] = "Displays active AI tools and usage on {host}.",
      ["AI-Requests"] = "Tracks all AI activity and request types on {host}.",
      ["AI-Providers"] = "Get AI providers, models and metadata info."
   };

}
