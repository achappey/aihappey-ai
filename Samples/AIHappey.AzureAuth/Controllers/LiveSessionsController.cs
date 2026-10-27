using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Authorize]
[Route("v1/live/sessions")]
public sealed class LiveSessionsController(IAIModelProviderResolver resolver) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(LiveSessionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status501NotImplemented)]
    public async Task<IActionResult> Post([FromBody] LiveSessionRequest request, CancellationToken cancellationToken)
    {
        var provider = await resolver.Resolve(request.Session.Model, cancellationToken);
        if (provider == null)
            return BadRequest(new { error = $"Model '{request.Session.Model}' is not available." });

        request.Session.Model = request.Session.Model.SplitModelId().Model;
        try
        {
            var response = await provider.CreateLiveSessionAsync(request, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, response);
        }
        catch (NotSupportedException ex)
        {
            return Problem(statusCode: StatusCodes.Status501NotImplemented, title: "Not Implemented", detail: ex.Message);
        }
    }
}
