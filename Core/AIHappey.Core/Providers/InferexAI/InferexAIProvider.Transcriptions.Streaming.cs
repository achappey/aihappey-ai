using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.InferexAI;

public sealed partial class InferexAIProvider
{
    internal Func<Uri, string, CancellationToken, Task<WebSocket>> TranscriptionSocketFactory { get; set; } = ConnectSocket;

    private static async Task<WebSocket> ConnectSocket(Uri uri, string key, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + key);
        try
        {
            await socket.ConnectAsync(uri, ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = NativeModel(request.Model);
        var key = ResolveKey();
        var catalog = await ListModels(cancellationToken);
        var advertised = catalog.SingleOrDefault(m => m.Id == $"{ProviderId}/{model}");
        if (advertised is null || advertised.Type != "transcription" || advertised.Tags?.Contains("streaming") != true)
            throw new NotSupportedException("The selected InferexAI model does not advertise realtime STT.");
        var audio = DecodeAudio(request.Audio);
        var options = Options(request.ProviderOptions);
        var encoding = request.InputAudioFormat?.Type switch
        {
            "pcm" or "audio/pcm" or "audio/L16" => "pcm_s16le",
            "audio/basic" => "mulaw",
            "pcm_s16le" or "mulaw" or "alaw" or "linear32" => request.InputAudioFormat.Type,
            _ => throw new NotSupportedException("InferexAI realtime STT requires raw pcm_s16le, mulaw, alaw or linear32 audio, not a container file.")
        };
        options["encoding"] = encoding;
        var rate = request.InputAudioFormat?.Rate is { } hz
            ? ProviderNumericConversions.ToInt32Exact(hz, "inputAudioFormat.rate")
            : options.TryGetValue("sample_rate", out var configuredRate)
                ? JsonSerializer.SerializeToElement(configuredRate).GetInt32() : 16000;
        if (rate is not (8000 or 16000)) throw new ArgumentException("InferexAI realtime sample_rate must be 8000 or 16000.");
        if (encoding == "pcm_s16le" && audio.Length % 2 != 0 || encoding == "linear32" && audio.Length % 4 != 0)
            throw new ArgumentException("Audio contains an incomplete raw sample.");
        options["sample_rate"] = rate;
        options["model"] = model;
        if (options.Remove("api_key")) throw new ArgumentException("WebSocket authentication uses the provider key, not query-string credentials.");
        if (options.Remove("language_code", out var language) && !options.ContainsKey("language"))
            options["language"] = language;
        var query = string.Join("&", options.Where(p => p.Value is not null).Select(p =>
        {
            var value = JsonSerializer.SerializeToElement(p.Value);
            return Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText());
        }));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(60));
        using var socket = await TranscriptionSocketFactory(new Uri("wss://api.inferexai.in/v1/audio/transcriptions/stream?" + query), key, linked.Token);
        var upload = UploadAudio(socket, audio, linked);
        var finals = new List<TranscriptionFinalPart>();
        var events = new List<JsonElement>();
        var completedIds = new HashSet<string>(StringComparer.Ordinal);
        var done = false;
        try
        {
            yield return new TranscriptionStreamStartPart();
            while (!done)
            {
                var message = await ReceiveWithUploadFailure(socket, upload, linked.Token, cancellationToken);
                if (message is null) throw new InvalidOperationException("InferexAI WebSocket closed before transcription_session.done.");
                using var document = JsonDocument.Parse(message);
                var root = document.RootElement;
                events.Add(root.Clone());
                if (request.IncludeRawChunks == true) yield return new TranscriptionRawPart { RawValue = root.Clone() };
                var type = String(root, "type");
                if (type == "error" || root.TryGetProperty("error", out _))
                    throw new InvalidOperationException($"InferexAI realtime transcription failed: {root}");
                if (type == "conversation.item.input_audio_transcription.delta")
                    yield return new TranscriptionDeltaPart { Id = String(root, "item_id"), Delta = root.GetProperty("delta").GetString()!, ProviderMetadata = Metadata(root) };
                else if (type == "conversation.item.input_audio_transcription.completed")
                {
                    var id = String(root, "item_id");
                    if (id is not null && !completedIds.Add(id)) continue;
                    var final = new TranscriptionFinalPart
                    {
                        Id = id, Text = root.GetProperty("transcript").GetString()!,
                        StartSecond = Number(root, "start"), EndSecond = Number(root, "end"), ProviderMetadata = Metadata(root)
                    };
                    finals.Add(final);
                    yield return final;
                }
                else
                {
                    yield return new TranscriptionResponseMetadataPart { Timestamp = DateTime.UtcNow, ModelId = request.Model, Body = root.Clone() };
                    done = type == "transcription_session.done";
                }
            }
            await upload;
            cancellationToken.ThrowIfCancellationRequested();
            if (socket.State == WebSocketState.Open)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cancellationToken);
            yield return new TranscriptionFinishPart
            {
                Text = string.Join(" ", finals.Select(f => f.Text)),
                Segments = finals.Where(f => f.StartSecond is not null && f.EndSecond is not null).Select(f =>
                    new TranscriptionSegment { Text = f.Text, StartSecond = f.StartSecond!.Value, EndSecond = f.EndSecond!.Value }).ToArray(),
                ProviderMetadata = Metadata(JsonSerializer.SerializeToElement(new { events })),
                Usage = events.Last().TryGetProperty("usage", out var usage)
                    ? usage.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) : null
            };
        }
        finally
        {
            linked.Cancel();
            socket.Abort();
            // Observe upload failure without replacing a receive/cancellation failure.
            try { await upload; } catch when (linked.IsCancellationRequested) { }
        }
    }

    private static async Task<string?> ReceiveWithUploadFailure(
        WebSocket socket, Task upload, CancellationToken linkedToken, CancellationToken callerToken)
    {
        try
        {
            return await ReceiveMessage(socket, linkedToken);
        }
        catch (OperationCanceledException) when (upload.IsFaulted && !callerToken.IsCancellationRequested)
        {
            await upload;
            throw;
        }
    }

    private static async Task UploadAudio(WebSocket socket, byte[] audio, CancellationTokenSource linked)
    {
        try
        {
            for (var offset = 0; offset < audio.Length; offset += 32768)
                await socket.SendAsync(new ArraySegment<byte>(audio, offset, Math.Min(32768, audio.Length - offset)),
                    WebSocketMessageType.Binary, true, linked.Token);
            await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("{\"type\":\"input_audio_buffer.commit\"}")),
                WebSocketMessageType.Text, true, linked.Token);
            linked.CancelAfter(TimeSpan.FromSeconds(60));
        }
        catch
        {
            linked.Cancel();
            throw;
        }
    }

    private static async Task<string?> ReceiveMessage(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (message.Length > 0) throw new InvalidOperationException("InferexAI closed within a WebSocket message.");
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidOperationException("InferexAI returned an unexpected binary WebSocket event.");
            message.Write(buffer, 0, result.Count);
            if (message.Length > 8 * 1024 * 1024) throw new InvalidOperationException("InferexAI WebSocket event exceeded 8 MB.");
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }
}
