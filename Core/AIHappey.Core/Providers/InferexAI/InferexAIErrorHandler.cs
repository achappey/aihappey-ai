namespace AIHappey.Core.Providers.InferexAI;

internal sealed class InferexAIErrorHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            return response;
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"InferexAI failed ({(int)response.StatusCode}): {body}", null, response.StatusCode);
        }
    }
}
