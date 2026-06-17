using System.Reflection;
using NetConduit.Enums;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.UnitTests;

/// <summary>
/// Lock-in coverage for lifecycle state guard gaps in StreamMultiplexer.
/// Covers: Start() after DisposeAsync (zombie multiplexer), and
/// OpenChannel/AcceptChannel/TryRegisterChannels TOCTOU with GoAwayAsync.
/// </summary>
public sealed class LifecycleGuardGapsTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static StreamMultiplexer CreateNeverStarted()
    {
        var duplex = new DuplexMemoryStream();
        return StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
    }

    private static async Task<(StreamMultiplexer Client, StreamMultiplexer Server)> CreateReadyPairAsync()
    {
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
        return (client, server);
    }

    [Fact]
    public async Task Start_AfterDisposeAsync_ThrowsObjectDisposedException()
    {
        var mux = CreateNeverStarted();
        mux.Start();
        await mux.DisposeAsync();

        var ex = Assert.Throws<ObjectDisposedException>(() => mux.Start());
        Assert.Contains("StreamMultiplexer", ex.Message);
    }

    [Fact]
    public async Task Start_AfterDisposeAsync_DoesNotCreateRunningTask()
    {
        var mux = CreateNeverStarted();
        mux.Start();
        await mux.DisposeAsync();

        try
        {
            mux.Start();
        }
        catch (ObjectDisposedException)
        {
            // Expected — verify no background task was started
        }

        Assert.False(mux.IsRunning);
    }

    [Fact]
    public async Task OpenChannel_AfterGoAwayAsync_ThrowsInvalidOperationException()
    {
        var (client, server) = await CreateReadyPairAsync();
        await client.GoAwayAsync();

        Assert.Throws<InvalidOperationException>(() => client.OpenChannel("after-goaway"));

        await server.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task AcceptChannel_AfterGoAwayAsync_ThrowsInvalidOperationException()
    {
        var (client, server) = await CreateReadyPairAsync();
        await server.GoAwayAsync();

        Assert.Throws<InvalidOperationException>(() => server.AcceptChannel("after-goaway"));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task TryRegisterChannels_AfterGoAwayAsync_ThrowsInvalidOperationException()
    {
        var (client, server) = await CreateReadyPairAsync();
        await client.GoAwayAsync();

        var reg = new ChannelRegistration[]
        {
            new() { ChannelId = "after-goaway", Direction = ChannelDirection.Outbound },
        };
        Assert.Throws<InvalidOperationException>(() => client.TryRegisterChannels(reg, out _));

        await server.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannel_ShutdownLatchedInsideLock_ThrowsInvalidOperationException()
    {
        // Deterministic TOCTOU test: simulate the race where OpenChannel passes
        // the pre-lock IsShuttingDown check but _isShuttingDown is latched
        // while OpenChannel is waiting for the registry lock.
        //
        // Thread A: acquire ChannelIndexLock → set _isShuttingDown → release
        // Thread B: call OpenChannel → pre-lock check passes (flag false) →
        //            blocks on lock → enters lock → without fix: creates channel
        var (client, server) = await CreateReadyPairAsync();

        object channelIndexLock = GetChannelIndexLock(client);
        var ready = new ManualResetEventSlim(false);
        var lockHeld = new ManualResetEventSlim(false);
        IChannel? racyChannel = null;
        Exception? racyException = null;

        var openTask = Task.Run(() =>
        {
            ready.Set();
            // Wait for Thread A to acquire the lock before calling OpenChannel.
            lockHeld.Wait(TimeSpan.FromSeconds(10));
            // Now call OpenChannel: pre-lock IsShuttingDown check passes
            // (flag is still false), then blocks on ChannelIndexLock.
            try
            {
                racyChannel = client.OpenChannel(new ChannelOptions { ChannelId = "racy" });
            }
            catch (Exception ex)
            {
                racyException = ex;
            }
        });

        ready.Wait(TimeSpan.FromSeconds(5));

        lock (channelIndexLock)
        {
            // Thread B is waiting for this signal. Once set, Thread B will
            // call OpenChannel, pass the pre-lock check, and block on this lock.
            lockHeld.Set();
            // Yield to let Thread B reach the lock.
            Thread.Sleep(100);
            // Thread B is now blocked on ChannelIndexLock. Latch shutdown.
            SetIsShuttingDown(client, value: true);
        }
        // Lock released — Thread B enters the lock. Without the fix,
        // it creates the channel without re-checking IsShuttingDown.

        await openTask.WaitAsync(TestTimeout);

        // The racy OpenChannel must have thrown because the inner re-check
        // catches the latched shutdown inside the lock.
        Assert.NotNull(racyException);
        Assert.IsType<InvalidOperationException>(racyException);
        Assert.Contains("GoAwayAsync", racyException.Message);

        await server.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task AcceptChannel_ShutdownLatchedInsideLock_ThrowsInvalidOperationException()
    {
        var (client, server) = await CreateReadyPairAsync();

        object acceptLock = GetAcceptLock(server);
        var ready = new ManualResetEventSlim(false);
        var lockHeld = new ManualResetEventSlim(false);
        IChannel? racyChannel = null;
        Exception? racyException = null;

        var acceptTask = Task.Run(() =>
        {
            ready.Set();
            lockHeld.Wait(TimeSpan.FromSeconds(10));
            try
            {
                racyChannel = server.AcceptChannel("racy");
            }
            catch (Exception ex)
            {
                racyException = ex;
            }
        });

        ready.Wait(TimeSpan.FromSeconds(5));

        lock (acceptLock)
        {
            lockHeld.Set();
            Thread.Sleep(100);
            SetIsShuttingDown(server, value: true);
        }

        await acceptTask.WaitAsync(TestTimeout);

        Assert.NotNull(racyException);
        Assert.IsType<InvalidOperationException>(racyException);
        Assert.Contains("GoAwayAsync", racyException.Message);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task TryRegisterChannels_ShutdownLatchedInsideLock_ThrowsInvalidOperationException()
    {
        var (client, server) = await CreateReadyPairAsync();

        object channelIndexLock = GetChannelIndexLock(client);
        var ready = new ManualResetEventSlim(false);
        var lockHeld = new ManualResetEventSlim(false);
        IReadOnlyDictionary<ChannelRegistration, IChannel>? racyChannels = null;
        bool? racyResult = null;
        Exception? racyException = null;

        var registerTask = Task.Run(() =>
        {
            ready.Set();
            lockHeld.Wait(TimeSpan.FromSeconds(10));
            try
            {
                var reg = new ChannelRegistration[]
                {
                    new() { ChannelId = "racy", Direction = ChannelDirection.Outbound },
                };
                racyResult = client.TryRegisterChannels(reg, out var channels);
                racyChannels = channels;
            }
            catch (Exception ex)
            {
                racyException = ex;
            }
        });

        ready.Wait(TimeSpan.FromSeconds(5));

        lock (channelIndexLock)
        {
            lockHeld.Set();
            Thread.Sleep(100);
            SetIsShuttingDown(client, value: true);
        }

        await registerTask.WaitAsync(TestTimeout);

        Assert.NotNull(racyException);
        Assert.IsType<InvalidOperationException>(racyException);
        Assert.Contains("GoAwayAsync", racyException.Message);

        await server.DisposeAsync();
        await client.DisposeAsync();
    }

    private static object GetChannelIndexLock(StreamMultiplexer mux)
    {
        var registry = GetPrivateField<object>(mux, "_registry");
        return GetPrivateField<object>(registry, "ChannelIndexLock");
    }

    private static object GetAcceptLock(StreamMultiplexer mux)
    {
        var registry = GetPrivateField<object>(mux, "_registry");
        return GetPrivateField<object>(registry, "AcceptLock");
    }

    private static void SetIsShuttingDown(StreamMultiplexer mux, bool value)
    {
        var field = typeof(StreamMultiplexer).GetField("_isShuttingDown",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Field _isShuttingDown not found.");
        field.SetValue(mux, value ? 1 : 0);
    }

    private static T GetPrivateField<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field {name} not found on {instance.GetType().Name}.");
        return (T)field.GetValue(instance)!;
    }
}
