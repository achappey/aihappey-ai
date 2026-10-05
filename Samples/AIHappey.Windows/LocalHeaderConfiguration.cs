using System.Collections.Frozen;
using System.Text.Json;

namespace AIHappey.Windows;

/// <summary>
/// Reads the same per-user flat HTTP-header object as the AIHappey CLI.
/// Loaded once at startup; it never writes or includes credentials in errors.
/// </summary>
public static class LocalHeaderConfiguration
{
    public static string GetDefaultPath()
    {
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("The local application-data directory is unavailable.");

        return Path.Combine(directory, "aihappey", "headers.json");
    }

    public static async Task<IReadOnlyDictionary<string, string>> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException();

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw new JsonException();

                headers[property.Name] = property.Value.GetString()!;
            }

            return headers.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }
        catch (FileNotFoundException)
        {
            return FrozenDictionary<string, string>.Empty;
        }
        catch (DirectoryNotFoundException)
        {
            return FrozenDictionary<string, string>.Empty;
        }
        catch (JsonException)
        {
            // Do not print parser diagnostics: malformed JSON can contain secrets.
            throw new InvalidOperationException($"Failed to load local HTTP headers from '{path}': expected a flat JSON object containing only string values.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Failed to load local HTTP headers from '{path}': the file could not be read.");
        }
    }
}
