using System.Net;
using System.Net.Sockets;

namespace NetConduit.Transport.Udp;

/// <summary>
/// Proof-of-receipt gate for the UDP accept path (Issue #616 hardening, Phase 2 scaffold).
/// Default <see cref="Disabled"/> preserves the existing v1 wire bytes; no challenge bytes
/// are emitted in this cut. <see cref="OptIn"/> accepts v1 (downgrade-visible future hook);
/// <see cref="Required"/> rejects v1 loudly.
/// </summary>
public enum UdpChallengeMode
{
    /// <summary>Existing v1 behavior: NC_HELLO/NC_HELLO_ACK only. No challenge bytes.</summary>
    Disabled,
    /// <summary>Accepts v1 (downgrade-visible future hook for challenge-capable clients).</summary>
    OptIn,
    /// <summary>Rejects v1 NC_HELLO loudly; no NC_HELLO_ACK is issued to legacy handshakes.</summary>
    Required,
}

/// <summary>
/// Accept-policy knobs for <see cref="UdpMultiplexer.CreateServerOptions(int, ReliableUdpOptions?, UdpAcceptOptions?)"/>.
/// Additive only; defaults reproduce today's fast singleton path plus a bounded competition window.
/// </summary>
public sealed record UdpAcceptOptions
{
    /// <summary>
    /// Competition window opened on the first admitted HELLO. Default 300ms (~1.5 client retry intervals).
    /// Bounds the race; never a global accept timeout (idle servers still wait on the caller's token).
    /// </summary>
    public TimeSpan VerificationWindow
    {
        get => _verificationWindow;
        init
        {
            if (value < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(VerificationWindow), value, "VerificationWindow must be non-negative.");
            _verificationWindow = value;
        }
    }

    private readonly TimeSpan _verificationWindow = TimeSpan.FromMilliseconds(300);

    /// <summary>Bounded candidate table size; oldest evicted first (LRU). Default 64 (mirrors drain cap).</summary>
    public int MaxCandidates
    {
        get => _maxCandidates;
        init
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxCandidates), value, "MaxCandidates must be positive.");
            _maxCandidates = value;
        }
    }

    private readonly int _maxCandidates = 64;

    /// <summary>Per-endpoint HELLO budget per sliding 1s window. Over-limit HELLOs are silently dropped. Default 20.</summary>
    public int MaxHellosPerEndpointPerSecond
    {
        get => _maxHellosPerEndpointPerSecond;
        init
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxHellosPerEndpointPerSecond), value, "Must be positive.");
            _maxHellosPerEndpointPerSecond = value;
        }
    }

    private readonly int _maxHellosPerEndpointPerSecond = 20;

    /// <summary>Global HELLO budget per sliding 1s window (back-pressure, not auth). Default 1000.</summary>
    public int MaxHellosGlobalPerSecond
    {
        get => _maxHellosGlobalPerSecond;
        init
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxHellosGlobalPerSecond), value, "Must be positive.");
            _maxHellosGlobalPerSecond = value;
        }
    }

    private readonly int _maxHellosGlobalPerSecond = 1000;

    /// <summary>Phase 2 gate. Default Disabled (wire-compat: old client still connects).</summary>
    public UdpChallengeMode ChallengeMode { get; init; } = UdpChallengeMode.Disabled;
}

/// <summary>
/// Wire-shape checks for the pre-commit accept path. Owns the canonical handshake bytes;
/// <see cref="UdpMultiplexer"/> never inlines <c>SequenceEqual</c> on the accept path again.
/// </summary>
internal static class UdpHandshakeProtocol
{
    private static readonly byte[] HelloBytes = "NC_HELLO"u8.ToArray();
    private static readonly byte[] HelloAckBytes = "NC_HELLO_ACK"u8.ToArray();

    public static byte[] Hello => (byte[])HelloBytes.Clone();
    public static byte[] HelloAck => (byte[])HelloAckBytes.Clone();

    public static bool IsHello(ReadOnlySpan<byte> buf) => buf.SequenceEqual(HelloBytes);

    public static bool IsHelloAck(ReadOnlySpan<byte> buf) => buf.SequenceEqual(HelloAckBytes);

    /// <summary>
    /// Heuristic for "this claimant proved liveness with real stream bytes": a plausible
    /// <see cref="ReliableUdpStream"/> DATA frame (7-byte header, FlagData set, wire-declared
    /// length matching the datagram). Pure ACK/FIN frames prove nothing (an ACK-all
    /// reflector or a FIN-only prober must not win the commit). Anything else non-HELLO
    /// from a known claimant is treated as a handshake violation (fail loudly,
    /// preserving #347), never as proof.
    /// </summary>
    public static bool LooksLikeDataFrame(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 7)
            return false;
        if ((buf[0] & 0x01) == 0)
            return false;
        int declared = (buf[5] << 8) | buf[6];
        return declared == buf.Length - 7;
    }
}

/// <summary>
/// Bounded per-factory candidate table: LRU eviction, per-endpoint + global sliding-window
/// HELLO caps, first-seen + data-proven tracking. Checked after shape validation, before
/// ACK-send, so floods cost one <c>SequenceEqual</c>, never a <c>Connect</c>.
/// </summary>
internal sealed class UdpAcceptTracker
{
    private sealed class Candidate
    {
        public IPEndPoint Endpoint = null!;
        public int HelloCount;
        public bool DataProven;
    }

    private readonly UdpAcceptOptions _options;
    private readonly Dictionary<string, LinkedListNode<Candidate>> _byKey = new(StringComparer.Ordinal);
    private readonly LinkedList<Candidate> _lru = new();
    private readonly Queue<DateTimeOffset> _globalHellos = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _perEndpointHellos = new(StringComparer.Ordinal);
    private string? _firstSeenKey;
    private string? _dataProvenKey;

    public UdpAcceptTracker(UdpAcceptOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    private static string Key(IPEndPoint ep) => ep.ToString();

    public bool IsKnown(IPEndPoint ep) => _byKey.ContainsKey(Key(ep));

    public int CandidateCount => _byKey.Count;

    public bool HasCompetition => _byKey.Count > 1;

    /// <summary>
    /// Shape-validated HELLO admission. Over-limit → <c>false</c> (caller silently drops, no ACK).
    /// </summary>
    public bool AdmitHello(IPEndPoint ep, DateTimeOffset now)
    {
        PruneGlobal(now);
        if (_globalHellos.Count >= _options.MaxHellosGlobalPerSecond)
            return false;

        var key = Key(ep);
        if (!_perEndpointHellos.TryGetValue(key, out var q))
        {
            q = new Queue<DateTimeOffset>();
            _perEndpointHellos[key] = q;
        }
        while (q.Count > 0 && (now - q.Peek()) >= TimeSpan.FromSeconds(1))
            q.Dequeue();
        if (q.Count >= _options.MaxHellosPerEndpointPerSecond)
            return false;

        if (!_byKey.TryGetValue(key, out var node))
        {
            while (_byKey.Count >= _options.MaxCandidates && _lru.First is not null)
                Evict(_lru.First);
            node = new LinkedListNode<Candidate>(new Candidate { Endpoint = ep });
            _lru.AddLast(node);
            _byKey[key] = node;
            _firstSeenKey ??= key;
        }
        else
        {
            _lru.Remove(node);
            _lru.AddLast(node);
        }

        node.Value.HelloCount++;
        q.Enqueue(now);
        _globalHellos.Enqueue(now);
        return true;
    }

    public void RecordDataProven(IPEndPoint ep)
    {
        var key = Key(ep);
        if (_byKey.TryGetValue(key, out var node))
        {
            node.Value.DataProven = true;
            _dataProvenKey ??= key;
        }
    }

    /// <summary>
    /// Live = a real retry loop (≥2 HELLOs, i.e. the ~200ms client retransmit) or stream data.
    /// Used only for grace-window migration (idle tentative → live competitor). A lone
    /// HELLO still commits as a singleton at its verification deadline: the
    /// StrayNonHello scar retransmits raw HELLO, stops on the first ACK, and expects
    /// commit with no data — a count-1 singleton at the deadline is indistinguishable
    /// from a blind datagram, so evicting it would park that scarred contract forever.
    /// Defeating the lone-blind-HELLO shape requires Phase C proof-of-receipt.
    /// </summary>
    public bool IsLive(IPEndPoint ep)
    {
        if (!_byKey.TryGetValue(Key(ep), out var node))
            return false;
        return node.Value.HelloCount >= 2 || node.Value.DataProven;
    }

    public bool TryGetLiveCompetitor(IPEndPoint tentative, out IPEndPoint? competitor)
    {
        competitor = null;
        var tentativeKey = Key(tentative);
        foreach (var kv in _byKey)
        {
            if (kv.Key == tentativeKey)
                continue;
            if (kv.Value.Value.HelloCount >= 2 || kv.Value.Value.DataProven)
            {
                competitor = kv.Value.Value.Endpoint;
                return true;
            }
        }
        return false;
    }

    public bool TryGetDataProven(out IPEndPoint? endpoint)
    {
        endpoint = null;
        if (_dataProvenKey is not null && _byKey.TryGetValue(_dataProvenKey, out var node))
        {
            endpoint = node.Value.Endpoint;
            return true;
        }
        return false;
    }

    public IPEndPoint? FirstSeen()
    {
        if (_firstSeenKey is not null && _byKey.TryGetValue(_firstSeenKey, out var node))
            return node.Value.Endpoint;
        return _lru.First?.Value.Endpoint;
    }

    private void Evict(LinkedListNode<Candidate> node)
    {
        var key = Key(node.Value.Endpoint);
        _lru.Remove(node);
        _byKey.Remove(key);
        _perEndpointHellos.Remove(key);
        if (key == _firstSeenKey)
            _firstSeenKey = _lru.First is not null ? Key(_lru.First.Value.Endpoint) : null;
        if (key == _dataProvenKey)
            _dataProvenKey = null;
    }

    private void PruneGlobal(DateTimeOffset now)
    {
        while (_globalHellos.Count > 0 && (now - _globalHellos.Peek()) >= TimeSpan.FromSeconds(1))
            _globalHellos.Dequeue();
        // Idle expiry for per-endpoint sliding-window queues so sprayed
        // endpoints that never return stay O(MaxCandidates)-bounded.
        List<string>? stale = null;
        foreach (var kv in _perEndpointHellos)
        {
            var q = kv.Value;
            while (q.Count > 0 && (now - q.Peek()) >= TimeSpan.FromSeconds(1))
                q.Dequeue();
            if (q.Count == 0 && !_byKey.ContainsKey(kv.Key))
                (stale ??= new List<string>()).Add(kv.Key);
        }
        if (stale is not null)
            foreach (var key in stale)
                _perEndpointHellos.Remove(key);
    }
}

/// <summary>
/// Pre-commit accept policy (Phase 1 server-only, wire-compat). Stays unconnected, ACKs every
/// admitted claimant via unconnected <c>SendTo</c> (same <c>NC_HELLO_ACK</c> bytes), commits the
/// data-proven claimant immediately, commits the singleton at its verification deadline
/// (preserves the StrayNonHello HELLO-alone contract), holds a competing first-seen through a
/// bounded grace window for a data-proven migrant, and never installs a global accept timeout
/// (idle servers still wait on the caller's token; only per-attempt verification deadlines
/// bound the competition window, enforced with a linked <c>CancellationTokenSource</c>).
/// </summary>
internal static class UdpHandshakeAcceptor
{
    public static async Task<IPEndPoint> AcceptAsync(
        UdpClient listener,
        UdpAcceptOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(options);

        var tracker = new UdpAcceptTracker(options);

        while (true)
        {
            // Competition window, anchored at the first admitted HELLO.
            DateTimeOffset windowDeadline = DateTimeOffset.MaxValue;
            bool windowOpen = false;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                TimeSpan? remaining = windowOpen ? windowDeadline - DateTimeOffset.UtcNow : null;
                if (remaining.HasValue && remaining.Value <= TimeSpan.Zero)
                    break;

                var result = await ReceiveAsync(listener, remaining, ct).ConfigureAwait(false);
                if (result is null)
                    break;

                var outcome = await ObserveAsync(listener, tracker, options, result.Value, ct).ConfigureAwait(false);
                if (outcome is not null)
                    return outcome;

                if (!windowOpen && tracker.CandidateCount > 0)
                {
                    windowOpen = true;
                    windowDeadline = DateTimeOffset.UtcNow + options.VerificationWindow;
                }
            }

            if (tracker.TryGetDataProven(out var proven) && proven is not null)
                return proven;

            if (tracker.CandidateCount == 0)
                continue;

            var tentative = tracker.FirstSeen();
            if (tentative is null)
                continue;

            if (!tracker.HasCompetition)
            {
                // Singleton commits HELLO-alone at its verification deadline.
                // This is the existing wire contract (StrayNonHello retransmits
                // raw HELLO and expects commit with no data): the test cancels
                // its send loop right after the first ACK, so a count-1
                // singleton MUST commit here — refusing it would park the
                // factory forever with no further datagrams coming. A lone
                // blind HELLO therefore still spends the one-shot (documented
                // residual; defeating it requires Phase C proof-of-receipt).
                return tentative;
            }

            // Bounded grace: a data-proven migrant may still take the tentative
            // commit while the socket stays unconnected (revocable only pre-Connect).
            // Expiry keeps first-seen (documented residual, requires Phase C).
            return await GraceAsync(listener, tracker, options, tentative, ct).ConfigureAwait(false);
        }
    }

    private static async Task<IPEndPoint> GraceAsync(
        UdpClient listener,
        UdpAcceptTracker tracker,
        UdpAcceptOptions options,
        IPEndPoint tentative,
        CancellationToken ct)
    {
        var graceDeadline = DateTimeOffset.UtcNow + options.VerificationWindow;

        while (true)
        {
            if (tracker.TryGetDataProven(out var proven) && proven is not null)
                return proven;

            if (tracker.IsLive(tentative))
                return tentative;

            if (tracker.TryGetLiveCompetitor(tentative, out var competitor) && competitor is not null)
            {
                // Tentative is idle (checked above) and a live competitor exists: migrate.
                tentative = competitor;
                continue;
            }

            var remaining = graceDeadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return tentative;

            ct.ThrowIfCancellationRequested();

            var result = await ReceiveAsync(listener, remaining, ct).ConfigureAwait(false);
            if (result is null)
                return tentative;

            var outcome = await ObserveAsync(listener, tracker, options, result.Value, ct).ConfigureAwait(false);
            if (outcome is not null)
                return outcome;
        }
    }

    /// <summary>
    /// Bounded receive: waits indefinitely on the caller's token when no deadline is armed
    /// (no global timeout), otherwise waits only until the per-attempt verification deadline
    /// via a linked CTS. Returns null on deadline expiry; caller cancellation propagates.
    /// </summary>
    private static async Task<UdpReceiveResult?> ReceiveAsync(
        UdpClient listener,
        TimeSpan? remaining,
        CancellationToken ct)
    {
        try
        {
            if (remaining.HasValue)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(remaining.Value);
                return await listener.ReceiveAsync(linked.Token).ConfigureAwait(false);
            }

            return await listener.ReceiveAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Single-datagram policy: HELLO → admit + ACK-all (rate caps applied after shape
    /// validation, before ACK-send); known-endpoint data frame → data-proven winner;
    /// known-endpoint junk → fail loudly (#347); unknown junk → discard (#306).
    /// Returns the commit endpoint only for immediately data-proven winners, else null.
    /// </summary>
    private static async Task<IPEndPoint?> ObserveAsync(
        UdpClient listener,
        UdpAcceptTracker tracker,
        UdpAcceptOptions options,
        UdpReceiveResult result,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var remote = result.RemoteEndPoint;

        if (UdpHandshakeProtocol.IsHello(result.Buffer))
        {
            if (options.ChallengeMode == UdpChallengeMode.Required)
            {
                throw new InvalidOperationException(
                    "UDP handshake rejected v1 NC_HELLO: server requires ChallengeMode.Required proof-of-receipt, " +
                    "so no NC_HELLO_ACK is issued to legacy handshakes. Use an OptIn/Disabled server or a challenge-capable client.");
            }

            if (!tracker.AdmitHello(remote, now))
                return null;

            try
            {
                await listener.SendAsync(UdpHandshakeProtocol.HelloAck, remote, ct).ConfigureAwait(false);
            }
            catch (SocketException)
            {
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }

            return null;
        }

        if (!tracker.IsKnown(remote))
            return null;

        if (UdpHandshakeProtocol.LooksLikeDataFrame(result.Buffer))
        {
            tracker.RecordDataProven(remote);
            return remote;
        }

        throw new InvalidOperationException(
            $"UDP peer sent {result.Buffer.Length}-byte non-handshake datagram before observing NC_HELLO_ACK; dropping connection to avoid silent data loss.");
    }
}
