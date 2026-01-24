using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Protocol;
using AIHappey.Telemetry;
using System.Text.Json.Nodes;
using AIHappey.Core.ModelProviders;
using AIHappey.AzureAuth.Extensions;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Route("sampling")]
public class SamplingController(IAIModelProviderResolver resolver, IChatTelemetryService chatTelemetryService) : ControllerBase
{
    private readonly IAIModelProviderResolver _resolver = resolver;

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Post([FromBody] CreateMessageRequestParams requestDto, CancellationToken cancellationToken)
    {
        //var model = requestDto.GetModel() ?? throw new Exception("Model missing");
        // var provider = await _resolver.Resolve(model, cancellationToken);

        var models = requestDto.ModelPreferences?.Hints?.Select(a => a.Name).OfType<string>() ?? [];
        IModelProvider? provider = null;

        foreach (var model in models)
        {
            try
            {
                provider = await _resolver.Resolve(model, cancellationToken);

                if (provider != null)
                    break;
            }
            catch (Exception)
            {
                //    provider = _resolver.GetProvider();
            }
        }

        provider ??= _resolver.GetProvider();



        var startedAt = DateTime.UtcNow;

        var result = await provider.SamplingAsync(requestDto, cancellationToken);

        var endedAt = DateTime.UtcNow;
        int inputTokens = 0;
        int totalTokens = 0;

        if (result?.Meta is JsonObject meta)
        {
            inputTokens = meta["inputTokens"]?.GetValue<int>() ?? 0;
            totalTokens = meta["totalTokens"]?.GetValue<int>() ?? 0;
        }

        await chatTelemetryService.TrackChatRequestAsync(new Common.Model.ChatRequest()
        {
            Model = result?.Model!,
            Temperature = requestDto.Temperature ?? 1,
        },
            HttpContext.GetUserOid()!,
            HttpContext.GetUserUpn()!,
            inputTokens,
            totalTokens,
            provider.GetIdentifier(),
            Telemetry.Models.RequestType.Sampling,
        startedAt, endedAt, cancellationToken);

        return Ok(result);
    }
}

