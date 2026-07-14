using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Extensions;

public static class SpeechExtensions
{
    public static SpeechRequest ToSpeechRequest(
       this AudioSpeechRequest request)
        => new()
        {
            Model = request.Model,
            Voice = request.Voice,
            Speed = request.Speed,
            OutputFormat = request.ResponseFormat,
            Instructions = request.Instructions,
            Text = request.Input
        };

}
