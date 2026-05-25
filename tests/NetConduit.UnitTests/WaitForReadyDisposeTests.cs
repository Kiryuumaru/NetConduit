using Xunit;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression for #395 and #401 — WaitForReadyAsync must always complete
/// once the mux has either reached Ready or been shut down.
///
/// Pre-fix, _readyTcs was only completed on TrySetResult (success) or
/// TrySetException (fatal). The cooperative-cancel path taken by DisposeAsync
/// before the first handshake completed left _readyTcs in
/// WaitingForActivation forever, pinning every awaiter without a token.
/// </summary>
public sealed class WaitForReadyDisposeTests
{
    private static StreamMultiplexer CreateNeverConnecting()
    {
        // StreamFactory never returns: this guarantees the first handshake
        // cannot complete, so _readyTcs only resolves on the dispose path.
        return StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
        });
    }

    [Fact]
    public async Task WaitForReadyAsync_DisposedBeforeReady_CompletesWithCancellation()
    {
        // Honest peer: mux is Started, StreamFactory blocks forever, then
        // DisposeAsync is called. The pre-#395/#401 bug pins the awaiter.
        var mux = CreateNeverConnecting();
        mux.Start();

        Task ready = mux.WaitForReadyAsync();

        await mux.DisposeAsync();

        // Bounded await: if the fix is missing, this throws TimeoutException
        // and the test fails fast instead of hanging the suite.
        var completed = await Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(ready, completed);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ready);
    }

    [Fact]
    public async Task WaitForReadyAsync_NeverStartedThenDisposed_CompletesWithCancellation()
    {
        // Edge case: a mux that was never Start()-ed. MainLoopAsync never
        // runs so the OCE catch never fires. The DisposeAsync site of the
        // fix is what unblocks this caller.
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new DuplexMemoryStream().SideA),
        });

        Task ready = mux.WaitForReadyAsync();

        await mux.DisposeAsync();

        var completed = await Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(ready, completed);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ready);
    }

    [Fact]
    public async Task WaitForReadyAsync_AfterReachingReadyThenDispose_StillCompletesSuccessfully()
    {
        // Invariant check: TrySetCanceled in the dispose path must be
        // idempotent vs. a prior successful TrySetResult, so a mux that
        // actually reached Ready still surfaces success to existing
        // awaiters even though dispose now calls TrySetCanceled.
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
        });

        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        // Take a fresh handle AFTER Ready, then dispose, then await it.
        Task afterReady = client.WaitForReadyAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();

        Assert.True(afterReady.IsCompletedSuccessfully);
    }
}
