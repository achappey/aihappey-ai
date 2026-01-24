using Microsoft.AspNetCore.Mvc;
using ModelContextProtocol.Protocol;
using AIHappey.Core.ModelProviders;

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
        var result = await provider.SamplingAsync(requestDto, cancellationToken);

        return Ok(result);
    }
}

