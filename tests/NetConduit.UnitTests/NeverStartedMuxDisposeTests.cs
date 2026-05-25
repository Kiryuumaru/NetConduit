using System.Reflection;
using NetConduit.Models;
using Xunit;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression for #376 — DisposeAsync on a never-started StreamMultiplexer must
/// dispose the constructor-allocated CancellationTokenSource and CoalescingSignal
/// instances. The previous early-return bailed out before any of those handles
/// were released, leaking kernel ManualResetEvent handles per discarded mux.
/// </summary>
public sealed class NeverStartedMuxDisposeTests
{
    private static StreamMultiplexer CreateNeverStarted()
    {
        var duplex = new DuplexMemoryStream();
        return StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
    }

    private static T GetPrivateField<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field {name} not found.");
        return (T)field.GetValue(instance)!;
    }

    [Fact]
    public async Task DisposeAsync_NeverStartedMux_DisposesInternalCancellationTokenSource()
    {
        var mux = CreateNeverStarted();

        await mux.DisposeAsync();

        // CancellationTokenSource.Dispose is observable: Token access throws
        // ObjectDisposedException afterwards. On master (pre-fix) the early-return
        // skipped _cts.Dispose() so Token access succeeded — that asymmetry was
        // the kernel-handle leak this test guards against.
        var cts = GetPrivateField<CancellationTokenSource>(mux, "_cts");
        Assert.Throws<ObjectDisposedException>(() => _ = cts.Token);
    }

    [Fact]
    public async Task DisposeAsync_NeverStartedMux_IsIdempotent()
    {
        var mux = CreateNeverStarted();

        await mux.DisposeAsync();
        await mux.DisposeAsync();
        await mux.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_NeverStartedMux_DoesNotFireDisconnected()
    {
        // A mux that was never Started never raised Connected. By contract it
        // must therefore not raise Disconnected on dispose. The fix gates the
        // final Disconnected event behind a wasStarted snapshot taken before
        // _isRunning is reset.
        var mux = CreateNeverStarted();

        int disconnectedCount = 0;
        mux.Disconnected += (_, _) => Interlocked.Increment(ref disconnectedCount);

        await mux.DisposeAsync();

        Assert.Equal(0, disconnectedCount);
    }
}
