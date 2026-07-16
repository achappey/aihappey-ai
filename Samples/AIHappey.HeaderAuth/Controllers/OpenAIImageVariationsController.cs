using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("v1/images/variations")]
public class OpenAIImageVariationsController(IAIModelProviderResolver resolver) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Post(CancellationToken cancellationToken)
    {
        try
        {
            var requestDto = Request.HasFormContentType
                ? (await Request.ReadFormAsync(cancellationToken)).ToOpenAIImageVariationRequest()
                : await Request.ReadFromJsonAsync<OpenAIImageVariationRequest>(cancellationToken) ?? new OpenAIImageVariationRequest();

            requestDto.Model = requestDto.ResolveOpenAIImageVariationModel();
            requestDto.ValidateOpenAIImageVariationRequest();

            HeaderAuthModelContext.SetActiveProvider(HttpContext, requestDto.Model);
            var provider = await resolver.Resolve(requestDto.Model, cancellationToken);
            if (provider == null)
                return BadRequest(new { error = $"Model '{requestDto.Model}' is not available." });

            requestDto.Model = requestDto.Model.SplitModelId().Model;
            try
            {
                var content = await provider.OpenAIImageVariationRequestAsync(requestDto, cancellationToken);
                return Ok(content);
            }
           catch (Exception ex) when (ex is NotImplementedException or NotSupportedException)
            {
                var imageRequest = await requestDto.ToImageRequest(requestDto.Model, provider.GetIdentifier(), cancellationToken);
                var content = await provider.ImageRequest(imageRequest, cancellationToken);
                return Ok(content.ToOpenAIImagesResponse(requestDto));
            }
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                error = new
                {
                    message = ex.Message,
                    type = "server_error",
                    code = (string?)null
                }
            });
        }
    }
}
