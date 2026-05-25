using System.Reflection;
using NetConduit.Interfaces;

namespace NetConduit.Transport.WebSocket.IntegrationTests;

/// <summary>
/// WebSocketMuxListener.CompletionStreamPair.DisposeAsync must complete
/// its TaskCompletionSource even when the inner IStreamPair.DisposeAsync throws.
/// Without this, HandleAsync hangs on completion.Task.WaitAsync until request
/// cancellation fires.
/// </summary>
public class CompletionStreamPairDisposeCompletionTests
{
    [Fact(Timeout = 5000)]
    public async Task DisposeAsync_InnerThrows_StillSignalsCompletion()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var throwingPair = new ThrowingStreamPair();

        var pairType = typeof(WebSocketMuxListener)
            .GetNestedType("CompletionStreamPair", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CompletionStreamPair nested type not found.");

        var ctor = pairType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
        var pair = (IAsyncDisposable)ctor.Invoke([throwingPair, completion]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pair.DisposeAsync());

        Assert.True(completion.Task.IsCompletedSuccessfully,
            "completion.TrySetResult must fire even when inner.DisposeAsync throws.");
        Assert.True(throwingPair.DisposeCalled);
    }

    private sealed class ThrowingStreamPair : IStreamPair
    {
        public bool DisposeCalled { get; private set; }
        public Stream ReadStream => Stream.Null;
        public Stream WriteStream => Stream.Null;

        public ValueTask DisposeAsync()
        {
            DisposeCalled = true;
            throw new InvalidOperationException("simulated inner dispose failure");
        }
    }
}
