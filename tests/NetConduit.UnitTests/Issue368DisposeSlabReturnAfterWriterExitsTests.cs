using System.Reflection;
using NetConduit.Interfaces;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression test for issue #368.
///
/// <see cref="StreamMultiplexer.DisposeAsync"/> previously called
/// <c>AbortAllChannels(MuxDisposed)</c> BEFORE awaiting <c>MainLoopTask</c>,
/// which returned each channel's slab to <c>ArrayPool&lt;byte&gt;.Shared</c>
/// while the writer thread could still be inside synchronous
/// <c>writeStream.Write(frames.Span)</c> — a use-after-free that corrupts
/// wire data when another <c>ArrayPool</c> consumer rents the same array.
///
/// The fix splits dispose into two phases: phase A marks channels closed
/// and wakes parked waiters (without returning slabs), phase B returns
/// slabs after <c>MainLoopTask</c> has awaited the writer task.
///
/// The test parks the writer inside <c>WriteStream.Write</c>, kicks off
/// <c>DisposeAsync</c>, and asserts the channel's slab has NOT been
/// returned to the pool while the writer is still mid-Write.
/// </summary>
public sealed class Issue368DisposeSlabReturnAfterWriterExitsTests
{
    [Fact(Timeout = 30_000)]
    public async Task DisposeAsync_DoesNotReturnWriteChannelSlab_WhileWriterIsMidWrite()
    {
        var releaseWriter = new ManualResetEventSlim(initialState: false);
        var writerEntered = new ManualResetEventSlim(initialState: false);

        var duplex = new DuplexMemoryStream();
        var blockingClient = new BlockingWriteStreamPair(duplex.SideA, releaseWriter, writerEntered);

        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(blockingClient),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
        });

        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
            MaxAutoReconnectAttempts = 0,
        });

        client.Start();
        server.Start();

        using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(
            client.WaitForReadyAsync(ready.Token),
            server.WaitForReadyAsync(ready.Token));

        var channel = client.OpenChannel("issue-368");

        // Wait for the channel to fully open so subsequent WriteAsync produces
        // user-data frames (not just an INIT control frame).
        using var openCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = await server.AcceptChannelAsync("issue-368", openCts.Token);
        await channel.WaitForReadyAsync(openCts.Token);

        // Queue some bytes to push the writer thread into a blocking Write call.
        await channel.WriteAsync(new byte[256]);

        Assert.True(writerEntered.Wait(TimeSpan.FromSeconds(5)),
            "Test precondition: writer thread must enter the blocking WriteStream.Write call.");

        var slabReturnedField = typeof(WriteChannel)
            .GetField("_slabReturned", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(slabReturnedField);

        // Kick off DisposeAsync. With the fix, this proceeds through phase A
        // (channels marked closed, slabs NOT returned), then blocks on
        // MainLoopTask which transitively awaits the parked writer task.
        var disposeTask = client.DisposeAsync().AsTask();

        // Give DisposeAsync time to run phase A (AbortAllChannels) and reach
        // the await on MainLoopTask. 250ms is well beyond the time needed to
        // execute the pre-fix synchronous AbortAllChannels-then-slab-return.
        await Task.Delay(250);

        Assert.False(disposeTask.IsCompleted,
            "DisposeAsync must still be awaiting the writer task; the writer is parked.");

        int slabReturnedDuringPhaseA = (int)slabReturnedField!.GetValue(channel)!;
        Assert.Equal(0, slabReturnedDuringPhaseA);

        // Release the writer; phase B runs and returns slabs.
        releaseWriter.Set();
        await disposeTask;

        int slabReturnedAfterPhaseB = (int)slabReturnedField.GetValue(channel)!;
        Assert.Equal(1, slabReturnedAfterPhaseB);

        await server.DisposeAsync();
        releaseWriter.Dispose();
        writerEntered.Dispose();
    }

    private sealed class BlockingWriteStreamPair(IStreamPair inner, ManualResetEventSlim release, ManualResetEventSlim entered) : IStreamPair
    {
        public Stream ReadStream { get; } = inner.ReadStream;
        public Stream WriteStream { get; } = new BlockingWriteStream(inner.WriteStream, release, entered);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class BlockingWriteStream(Stream inner, ManualResetEventSlim release, ManualResetEventSlim entered) : Stream
    {
        // Once true, subsequent Writes pass straight through. We only need to
        // park the first user-data write that follows the handshake/control
        // traffic so the test does not deadlock the handshake itself.
        private int _userWriteSeen;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            MaybePark(count);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            MaybePark(buffer.Length);
            inner.Write(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            MaybePark(buffer.Length);
            await inner.WriteAsync(buffer, ct).ConfigureAwait(false);
        }

        private void MaybePark(int count)
        {
            // Heuristic: user data write batches will be >= 256 bytes (our test
            // payload). Control / handshake frames are smaller. Park exactly
            // once on the first qualifying write so the test can synchronize.
            if (count < 200) return;
            if (Interlocked.Exchange(ref _userWriteSeen, 1) != 0) return;
            entered.Set();
            release.Wait();
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
