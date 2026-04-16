using System.Globalization;

namespace AIHappey.Abstractions.Http;

public static class ProviderBackendCapture
{
    private static readonly Lock Sync = new();
    private static ProviderBackendCaptureOptions _options = new();

    public static ProviderBackendCaptureOptions Current
    {
        get
        {
            lock (Sync)
            {
                return Clone(_options);
            }
        }
    }

    public static void Configure(ProviderBackendCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (Sync)
        {
            _options = Clone(options);
        }
    }

    public static void Disable()
        => Configure(new ProviderBackendCaptureOptions());

    public static void ConfigureDevelopmentDefaults(string contentRootPath, string? relativeRootDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        if (!IsDevelopmentEnvironment())
        {
            Disable();
            return;
        }

        Configure(new ProviderBackendCaptureOptions
        {
            Enabled = true,
            DevelopmentOnly = true,
            RootDirectory = Path.Combine(
                contentRootPath,
                relativeRootDirectory ?? Path.Combine("captures", "provider-raw"))
        });
    }

    public static ProviderBackendCaptureSink? BeginStreamCapture(
        string endpointFamily,
        HttpResponseMessage response,
        ProviderBackendCaptureRequest? request = null)
    {
        if (!TryResolvePath(endpointFamily, response, request, isStream: true, out var path))
            return null;

        return new ProviderBackendCaptureSink(path, CreateWriter(path));
    }

    public static async Task<string?> CaptureJsonAsync(
        string endpointFamily,
        HttpResponseMessage response,
        string body,
        ProviderBackendCaptureRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(body);

        if (!TryResolvePath(endpointFamily, response, request, isStream: false, out var path))
            return null;

        await using var writer = CreateWriter(path);
        await writer.WriteAsync(body.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
        return path;
    }

    private static bool TryResolvePath(
        string endpointFamily,
        HttpResponseMessage response,
        ProviderBackendCaptureRequest? request,
        bool isStream,
        out string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointFamily);
        ArgumentNullException.ThrowIfNull(response);

        var options = Current;
        var enabled = request?.Enabled ?? options.Enabled;

        if (!enabled || (options.DevelopmentOnly && !IsDevelopmentEnvironment()))
        {
            path = string.Empty;
            return false;
        }

        var rootDirectory = string.IsNullOrWhiteSpace(options.RootDirectory)
            ? Path.Combine("captures", "provider-raw")
            : options.RootDirectory;

        var directory = string.IsNullOrWhiteSpace(request?.RelativeDirectory)
            ? Path.Combine(rootDirectory, endpointFamily, "raw")
            : ResolveRelativeDirectory(rootDirectory, request.RelativeDirectory);

        Directory.CreateDirectory(directory);

        var fileName = string.IsNullOrWhiteSpace(request?.FileName)
            ? GenerateFileName(endpointFamily, response, isStream)
            : NormalizeFileName(request.FileName!, isStream);

        path = Path.Combine(directory, fileName);
        return true;
    }

    private static StreamWriter CreateWriter(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.Asynchronous);

        return new StreamWriter(stream)
        {
            AutoFlush = true
        };
    }

    private static string ResolveRelativeDirectory(string rootDirectory, string relativeDirectory)
    {
        if (Path.IsPathRooted(relativeDirectory))
            throw new InvalidOperationException("Capture override directory must be relative to the configured capture root.");

        var fullRoot = Path.GetFullPath(rootDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativeDirectory));

        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Capture override directory must stay within the configured capture root.");

        return fullPath;
    }

    private static string GenerateFileName(string endpointFamily, HttpResponseMessage response, bool isStream)
    {
        var requestUri = response.RequestMessage?.RequestUri;
        var host = requestUri is null || string.IsNullOrWhiteSpace(requestUri.Host)
            ? "unknown-host"
            : SanitizeSegment(requestUri.Host);
        var suffix = isStream ? "stream" : "response";
        var extension = isStream ? ".jsonl" : ".json";
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

        return $"{SanitizeSegment(endpointFamily)}-{suffix}-{host}-{stamp}{extension}";
    }

    private static string NormalizeFileName(string fileName, bool isStream)
    {
        var normalized = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("Capture override file name must not be empty.");

        return Path.HasExtension(normalized)
            ? normalized
            : normalized + (isStream ? ".jsonl" : ".json");
    }

    private static string SanitizeSegment(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var index = 0;

        foreach (var ch in value)
        {
            buffer[index++] = char.IsLetterOrDigit(ch) || ch is '-' or '_'
                ? ch
                : '-';
        }

        return new string(buffer[..index]).Trim('-');
    }

    private static bool IsDevelopmentEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        return string.Equals(value, "Development", StringComparison.OrdinalIgnoreCase);
    }

    private static ProviderBackendCaptureOptions Clone(ProviderBackendCaptureOptions options)
        => new()
        {
            Enabled = options.Enabled,
            DevelopmentOnly = options.DevelopmentOnly,
            RootDirectory = options.RootDirectory
        };
}

public sealed class ProviderBackendCaptureSink(string filePath, StreamWriter writer) : IAsyncDisposable
{
    public string FilePath { get; } = filePath;

    public async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default)
        => await writer.WriteLineAsync(line.AsMemory(), cancellationToken);

    public ValueTask DisposeAsync()
        => writer.DisposeAsync();
}
