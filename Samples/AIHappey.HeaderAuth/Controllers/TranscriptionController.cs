using AIHappey.Core.AI;
using Microsoft.AspNetCore.Mvc;
using OpenAI;

namespace AIHappey.HeaderAuth.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TranscriptionController(IApiKeyResolver apiKeyResolver) : ControllerBase
{
    [HttpPost("transcribe")]
    public async Task<IActionResult> Transcribe([FromForm] IFormFile audio)
    {
        if (audio == null || audio.Length == 0)
            return BadRequest("No audio file uploaded.");

        using var content = new MultipartFormDataContent();
        using var stream = audio.OpenReadStream();
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
        content.Add(fileContent, "audio", audio.FileName);
        var openClient = new OpenAIClient(
                  apiKeyResolver.Resolve("openai"));
        var audioClient = openClient.GetAudioClient("gpt-4o-transcribe");
        var result = await audioClient.TranscribeAudioAsync(stream, audio.FileName, new OpenAI.Audio.AudioTranscriptionOptions());

        return new JsonResult(result.Value);
    }
}
