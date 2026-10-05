using System.Text;

namespace AIHappey.Core.Diagnostics;

/// <summary>
/// Observes only reads requested by the native parser. Incrementally decodes UTF-8
/// SSE into readable native frames, before parsing. Retains only the current frame,
/// preserves original line endings and split characters, and never owns the inner stream.
/// </summary>
public sealed class DebugResponseStream(Stream inner, IProviderDebugEmitter emitter,
    string provider, string operation, string operationId, string mediaType) : Stream
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _frame = new();
    private int _lineLength;
    private bool _pendingCr;
    private bool _ended;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = await inner.ReadAsync(buffer, cancellationToken);
        if (emitter.Enabled && (count > 0 || buffer.Length > 0))
            await ObserveAsync(buffer[..count], count == 0, cancellationToken);
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (emitter.Enabled && (read > 0 || count > 0))
            ObserveAsync(buffer.AsMemory(offset, read), read == 0, default).AsTask().GetAwaiter().GetResult();
        return read;
    }

    private async ValueTask ObserveAsync(ReadOnlyMemory<byte> bytes, bool eof, CancellationToken cancellationToken)
    {
        if (_ended) return;
        var chars = new char[bytes.Length + 2];
        var count = _decoder.GetChars(bytes.Span, chars, flush: eof);
        for (var i = 0; i < count; i++)
        {
            var character = chars[i];
            if (_pendingCr)
            {
                _pendingCr = false;
                if (character == '\n')
                {
                    _frame.Append(character);
                    await EndLineAsync(cancellationToken);
                    continue;
                }
                await EndLineAsync(cancellationToken);
            }

            _frame.Append(character);
            if (character == '\r') _pendingCr = true;
            else if (character == '\n') await EndLineAsync(cancellationToken);
            else _lineLength++;
        }

        if (eof)
        {
            _ended = true;
            if (_pendingCr)
            {
                _pendingCr = false;
                await EndLineAsync(cancellationToken);
            }
            if (_frame.Length > 0)
                await EmitFrameAsync(complete: false, cancellationToken);
        }
    }

    private async ValueTask EndLineAsync(CancellationToken cancellationToken)
    {
        if (_lineLength == 0)
            await EmitFrameAsync(complete: true, cancellationToken);
        _lineLength = 0;
    }

    private async ValueTask EmitFrameAsync(bool complete, CancellationToken cancellationToken)
    {
        var raw = _frame.ToString();
        _frame.Clear();
        string? name = null, id = null, retry = null;
        var data = new List<string>();
        foreach (var line in raw.Split(["\r\n", "\r", "\n"], StringSplitOptions.None))
        {
            if (line.Length == 0 || line[0] == ':') continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            switch (field)
            {
                case "event": name = value; break;
                case "id": id = value; break;
                case "retry": retry = value; break;
                case "data": data.Add(value); break;
            }
        }
        await emitter.EmitAsync(provider, operation, operationId, "response-event",
            new(raw, "text", mediaType)
            {
                EventName = name,
                EventId = id,
                Retry = retry,
                Complete = complete,
                Data = data.Count > 0 ? ProviderDebugPayload.ParseData(string.Join("\n", data)) : null
            }, cancellationToken);
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
