using NetConduit.Interfaces;

namespace NetConduit.UnitTests;

/// <summary>
/// Coordinates paired transports for reconnection tests.
/// Pre-creates N rounds of DuplexMemoryStream pairs with killable wrappers.
/// Each factory call (client or server) consumes the next round in sequence.
/// </summary>
internal sealed class TransportBroker
{
    private readonly (KillableStreamPair Client, KillableStreamPair Server)[] _pairs;
    private int _clientCallCount = -1;
    private int _serverCallCount = -1;

    public TransportBroker(int rounds)
    {
        _pairs = new (KillableStreamPair, KillableStreamPair)[rounds];
        for (int i = 0; i < rounds; i++)
        {
            var duplex = new DuplexMemoryStream();
            _pairs[i] = (
                new KillableStreamPair(duplex.SideA),
                new KillableStreamPair(duplex.SideB)
            );
        }
    }

    public Task<IStreamPair> ClientFactory(CancellationToken ct)
    {
        int idx = Interlocked.Increment(ref _clientCallCount);
        return Task.FromResult<IStreamPair>(_pairs[idx].Client);
    }

    public Task<IStreamPair> ServerFactory(CancellationToken ct)
    {
        int idx = Interlocked.Increment(ref _serverCallCount);
        return Task.FromResult<IStreamPair>(_pairs[idx].Server);
    }

    /// <summary>Kill both sides of the specified round's transport.</summary>
    public void KillRound(int round)
    {
        _pairs[round].Client.Kill();
        _pairs[round].Server.Kill();
    }
}
