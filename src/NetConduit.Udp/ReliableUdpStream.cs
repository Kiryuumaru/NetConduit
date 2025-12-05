using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading.Channels;
using NetConduit;

namespace NetConduit.Udp;

/// <summary>
/// Minimal reliable, ordered stream over UDP for use with <see cref="StreamMultiplexer"/>.
/// Implements stop-and-wait with per-packet ack and retransmit.
/// </summary>
internal sealed class ReliableUdpStream : Stream
{
    private const byte FlagData = 0x01;
    private const byte FlagAck = 0x02;
    private const byte FlagFin = 0x04;

    private readonly UdpClient _udp;
    private readonly ReliableUdpOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<byte[]> _receiveChannel;
    private readonly object _sendLock = new();

    private Task? _receiveLoop;
    private uint _sendSeq = 1;
    private uint _expectedSeq = 1;
    private TaskCompletionSource<uint>? _pendingAck;
    private volatile bool _finReceived;
    private volatile bool _disposed;
    private byte[]? _currentBuffer;
    private int _currentOffset;

    public ReliableUdpStream(UdpClient udp, ReliableUdpOptions? options = null)
    {
        _udp = udp ?? throw new ArgumentNullException(nameof(udp));
        _options = options ?? new ReliableUdpOptions();
        _receiveChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0)
            return 0;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        while (true)
        {
            if (_currentBuffer is not null && _currentOffset < _currentBuffer.Length)
            {
                var toCopy = Math.Min(buffer.Length, _currentBuffer.Length - _currentOffset);
                _currentBuffer.AsSpan(_currentOffset, toCopy).CopyTo(buffer.Span);
                _currentOffset += toCopy;
                if (_currentOffset >= _currentBuffer.Length)
                {
                    _currentBuffer = null;
                    _currentOffset = 0;
                }
                return toCopy;
            }

            if (_finReceived && _receiveChannel.Reader.TryPeek(out _) == false)
                return 0;

            try
            {
                var item = await _receiveChannel.Reader.ReadAsync(linkedCts.Token).ConfigureAwait(false);
                _currentBuffer = item;
                _currentOffset = 0;
            }
            catch (ChannelClosedException)
            {
                return 0;
            }
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0)
            return;

        var maxPayload = Math.Max(1, _options.Mtu - 7); // 7-byte header
        var remaining = buffer;
        while (remaining.Length > 0)
        {
            var chunk = remaining.Length > maxPayload ? remaining[..maxPayload] : remaining;
            await SendWithAckAsync(chunk, FlagData, cancellationToken).ConfigureAwait(false);
            remaining = remaining[chunk.Length..];
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _udp.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try
        {
            await SendWithAckAsync(ReadOnlyMemory<byte>.Empty, FlagFin, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // ignore send failures on dispose
        }
        _udp.Dispose();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await _udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (result.Buffer.Length < 7)
                    continue;

                var span = result.Buffer.AsSpan();
                var flags = span[0];
                var seq = BinaryPrimitives.ReadUInt32BigEndian(span[1..5]);
                var len = BinaryPrimitives.ReadUInt16BigEndian(span[5..7]);
                var payload = span.Slice(7, Math.Min(len, span.Length - 7)).ToArray();

                if ((flags & FlagAck) == FlagAck)
                {
                    _pendingAck?.TrySetResult(seq);
                    continue;
                }

                if ((flags & FlagData) == FlagData)
                {
                    if (seq == _expectedSeq)
                    {
                        _expectedSeq++;
                        _receiveChannel.Writer.TryWrite(payload);
                    }
                    // Always ack the latest sequence we consider accepted
                    await SendAckAsync(seq, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if ((flags & FlagFin) == FlagFin)
                {
                    _finReceived = true;
                    _receiveChannel.Writer.TryComplete();
                    await SendAckAsync(seq, cancellationToken).ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _receiveChannel.Writer.TryComplete();
        }
        catch
        {
            _receiveChannel.Writer.TryComplete();
        }
    }

    private async Task SendWithAckAsync(ReadOnlyMemory<byte> payload, byte flags, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        lock (_sendLock)
        {
            _pendingAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var attempts = 0;
        while (true)
        {
            linkedCts.Token.ThrowIfCancellationRequested();
            attempts++;
            await SendPacketAsync(_sendSeq, flags, payload, linkedCts.Token).ConfigureAwait(false);

            try
            {
                using var ackCts = new CancellationTokenSource(_options.RetransmitTimeout);
                using var combined = CancellationTokenSource.CreateLinkedTokenSource(ackCts.Token, linkedCts.Token);
                var acked = await (_pendingAck?.Task ?? Task.FromResult<uint>(0)).WaitAsync(combined.Token).ConfigureAwait(false);
                if (acked == _sendSeq)
                {
                    _sendSeq++;
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                if (attempts > _options.MaxRetransmits)
                    throw new TimeoutException($"UDP send timed out after {attempts} attempts");
            }
        }
    }

    private Task SendAckAsync(uint seq, CancellationToken cancellationToken)
        => SendPacketAsync(seq, FlagAck, ReadOnlyMemory<byte>.Empty, cancellationToken);

    private async Task SendPacketAsync(uint seq, byte flags, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[7];
        header[0] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(header[1..5], seq);
        BinaryPrimitives.WriteUInt16BigEndian(header[5..7], (ushort)payload.Length);

        var buffer = new byte[header.Length + payload.Length];
        header.CopyTo(buffer);
        payload.CopyTo(buffer.AsMemory(header.Length));

        await _udp.SendAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
}
