using AIHappey.Core.Contracts;
using Microsoft.AspNetCore.Mvc;
using ModelContextProtocol.Protocol;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("sampling")]
public class SamplingController(IAIModelProviderResolver resolver) : ControllerBase
{
    private readonly IAIModelProviderResolver _resolver = resolver;

    [HttpPost]
    public async Task<IActionResult> Post(
     [FromBody] CreateMessageRequestParams requestDto,
     CancellationToken cancellationToken)
    {
        var modelHints = requestDto.ModelPreferences?.Hints?
            .Select(a => a.Name)
            .OfType<string>()
            .ToList() ?? [];

        if (modelHints.Count == 0)
            return BadRequest("Sampling requires at least one model hint.");

        Exception? lastException = null;
        IModelProvider? provider = null;
        CreateMessageResult? result = null;

        foreach (var model in modelHints)
        {
            try
            {
                HeaderAuthModelContext.SetActiveProvider(HttpContext, model);

                provider = await _resolver.Resolve(model, cancellationToken);

                if (provider == null)
                    continue;

                result = await provider.SamplingAsync(requestDto, cancellationToken);
                break;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        if (result == null)
        {
            HeaderAuthModelContext.ClearActiveProvider(HttpContext);

            provider = _resolver.GetProvider();

            try
            {
                result = await provider.SamplingAsync(requestDto, cancellationToken);
            }
            catch (Exception ex)
            {
                throw lastException ?? ex;
            }
        }

        return Ok(result);
    }
}

