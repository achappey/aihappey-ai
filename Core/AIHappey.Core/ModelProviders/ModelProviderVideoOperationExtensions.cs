namespace AIHappey.Core.ModelProviders;

/// <summary>
/// Temporary asynchronous-video seam. Provider-native start/status methods
/// will replace these throwing extensions as providers are migrated.
/// </summary>
public static class ModelProviderVideoOperationExtensions
{
  /*  public static Task<VideoOperationStartResult> StartVideoOperation(
        this IModelProvider provider,
        VideoRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"Provider '{provider.GetIdentifier()}' does not support asynchronous video generation yet.");

    public static Task<VideoOperationStatusResult> GetVideoOperationStatus(
        this IModelProvider provider,
        string operation,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"Provider '{provider.GetIdentifier()}' does not support asynchronous video generation yet.");*/
}
