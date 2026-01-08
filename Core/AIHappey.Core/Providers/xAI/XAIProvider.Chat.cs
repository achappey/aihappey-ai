using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text;
using AIHappey.Common.Extensions;
using System.Net.Mime;
using System.Dynamic;
using AIHappey.Common.Model.Providers.XAI;

namespace AIHappey.Core.Providers.xAI;

public partial class XAIProvider : IModelProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        // using var req = chatRequest.BuildXAIStreamRequest(GetIdentifier());

        var tools = new List<dynamic>();

        var metadata = chatRequest.GetProviderMetadata<XAIProviderMetadata>(GetIdentifier());

        if (metadata?.XSearch != null)
            tools.Add(metadata.XSearch);

        if (metadata?.WebSearch != null)
            tools.Add(metadata.WebSearch);

        if (metadata?.CodeExecution != null)
            tools.Add(metadata.CodeExecution);

        foreach (var tool in chatRequest.Tools ?? [])
        {
            tools.Add(new
            {
                type = "function",
                name = tool.Name,
                description = tool.Description,
                parameters = tool.InputSchema
            });
        }

        dynamic payload = new ExpandoObject();
        payload.model = chatRequest.Model;
        payload.stream = true;
        payload.temperature = chatRequest.Temperature;
        payload.reasoning = metadata?.Reasoning;
        payload.instructions = metadata?.Instructions;
        payload.store = false;
        payload.parallel_tool_calls = metadata?.ParallelToolCalls;
        payload.input = chatRequest.Messages.BuildResponsesInput();
        payload.tools = tools;

        if (chatRequest.ResponseFormat != null)
        {
            payload.text = new
            {
                format = chatRequest.ResponseFormat
            };
        }

        if (tools.Count > 0)
            payload.tool_choice = "auto";

        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        var req = new HttpRequestMessage(HttpMethod.Post, "v1/responses")
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        // ---------- 2) Send and read SSE ----------
        using var resp = await _client.SendAsync(
            req,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception(string.IsNullOrWhiteSpace(err) ? resp.ReasonPhrase : err);
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);

        await foreach (var part in OpenAIResponsesStreamReader.ReadAsync(
            stream,
            chatRequest,
            chatRequest.Model,
            providerTools: tools.Where(a => chatRequest.Tools?.Any(z => z.Name == a.name) != true)
            .Select(z => z.Name)
            .OfType<string>() ?? [],
            cancellationToken))
        {
            yield return part;
        }
    }

}
