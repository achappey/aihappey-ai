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
    public async Task<IActionResult> Post(
        [FromBody] CreateMessageRequestParams requestDto,
        CancellationToken cancellationToken)
    {
        var modelHints = requestDto.ModelPreferences?.Hints?
            .Select(a => a.Name)
            .OfType<string>()
            .ToList() ?? [];

        if (!modelHints.Any())
            return BadRequest("Sampling requires at least one model hint.");

        Exception? lastException = null;
        CreateMessageResult? result = null;
        IModelProvider? provider = null;

        foreach (var model in modelHints)
        {
            try
            {
                provider = await _resolver.Resolve(model, cancellationToken);

                if (provider == null)
                    continue;

                result = await provider.SamplingAsync(
                    requestDto,
                    cancellationToken);

                break;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        if (result == null)
        {
            provider ??= _resolver.GetProvider();

            try
            {
                result = await provider.SamplingAsync(
                    requestDto,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                throw lastException ?? ex;
            }
        }

        var startedAt = DateTime.UtcNow;
        var endedAt = DateTime.UtcNow;

        var usageNode = result?.Meta?["usage"];

        int inputTokens =
            usageNode?["promptTokens"]?.GetValue<int>() ?? 0;

        int totalTokens =
            usageNode?["totalTokens"]?.GetValue<int>() ?? 0;

        await chatTelemetryService.TrackChatRequestAsync(
            new Vercel.Models.ChatRequest
            {
                Model = result?.Model!.SplitModelId().Model!,
                Temperature = requestDto.Temperature ?? 1,
            },
            HttpContext.GetUserOid()!,
            HttpContext.GetUserUpn()!,
            inputTokens,
            totalTokens,
            provider!.GetIdentifier(),
            Telemetry.Models.RequestType.Sampling,
            startedAt,
            endedAt,
            HttpContext.GetAgentId(),
            cancellationToken);

        return Ok(result);
    }
}

