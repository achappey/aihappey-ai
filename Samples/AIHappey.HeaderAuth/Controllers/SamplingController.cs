using AIHappey.Core.Contracts;
using Microsoft.AspNetCore.Mvc;
using ModelContextProtocol.Protocol;
using AIHappey.HeaderAuth;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("sampling")]
public class SamplingController(IAIModelProviderResolver resolver) : ControllerBase
{
    private readonly IAIModelProviderResolver _resolver = resolver;

    [HttpPost]
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
                HeaderAuthModelContext.SetActiveProvider(HttpContext, model);
                provider = await _resolver.Resolve(model, cancellationToken);

                if (provider != null)
                    break;
            }
            catch (Exception)
            {
            }
        }

        if (provider == null)
            HeaderAuthModelContext.ClearActiveProvider(HttpContext);

        provider ??= _resolver.GetProvider();
        var result = await provider.SamplingAsync(requestDto, cancellationToken);

        return Ok(result);
    }
}

