using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Protocol;
using AIHappey.Telemetry;
using AIHappey.AzureAuth.Extensions;
using AIHappey.Core.Contracts;
using AIHappey.Core.AI;

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
        var models = requestDto.ModelPreferences?.Hints?.Select(a => a.Name).OfType<string>() ?? [];
        IModelProvider? provider = null;

        if (!models.Any())
            return BadRequest("Sampling requires at least one model hint.");

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
            }
        }

        provider ??= _resolver.GetProvider();

        var startedAt = DateTime.UtcNow;
        var modelHint = requestDto.ModelPreferences?.Hints?.FirstOrDefault(a => a.Name?.StartsWith(provider.GetIdentifier()) == true);
      /*  requestDto.ModelPreferences?.Hints = [ new ModelHint()
            {
                Name = modelHint?.Name?.SplitModelId().Model
            }];*/

        var result = await provider.SamplingAsync(requestDto, cancellationToken);

        var endedAt = DateTime.UtcNow;
        var usageNode = (result?.Meta)?["usage"];

        int inputTokens = usageNode?["promptTokens"]?.GetValue<int>() ?? 0;
        int totalTokens = usageNode?["totalTokens"]?.GetValue<int>() ?? 0;

        await chatTelemetryService.TrackChatRequestAsync(new Vercel.Models.ChatRequest()
        {
            Model = result?.Model!.SplitModelId().Model!,
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

