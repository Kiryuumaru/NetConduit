using NetConduit.Enums;
using NetConduit.Exceptions;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Internal;

/// <summary>
/// Batch channel-registration coordinator for <see cref="StreamMultiplexer"/>.
/// Implements the three-phase commit used by
/// <see cref="StreamMultiplexer.TryRegisterChannels(System.ReadOnlySpan{ChannelRegistration}, out System.Collections.Generic.IReadOnlyDictionary{ChannelRegistration, IChannel})"/>:
/// (1) validate every registration up-front, (2) commit under the registry's
/// AcceptLock so the batch is serialized against single-channel accepts and
/// the reader's INIT-arrival adoption, (3) emit INIT frames + stats updates
/// for freshly-committed write channels. Any Phase-2 collision rolls back
/// every prior commit from the same batch.
/// </summary>
internal sealed class ChannelBatchRegistrar(
    ChannelRegistry registry,
    MultiplexerOptions options,
    MultiplexerStats stats,
    IChannelOwner owner)
{
    /// <summary>
    /// Atomically register a batch of channels. Returns <c>false</c> if any
    /// outbound id collides with an existing write/read/pending-accept entry
    /// (every prior commit in the same batch is rolled back). Throws if any
    /// registration fails Phase-1 validation.
    /// </summary>
    /// <param name="registrations">Registrations to commit.</param>
    /// <param name="isConnected">Snapshot of the mux's <c>_isConnected</c>
    /// flag at the entry of the call. Freshly-committed channels are
    /// transitioned to <c>Connected</c> when true.</param>
    /// <param name="channels">On success, maps each registration to its
    /// committed channel (write or read).</param>
    internal bool TryRegisterChannels(
        ReadOnlySpan<ChannelRegistration> registrations,
        bool isConnected,
        out IReadOnlyDictionary<ChannelRegistration, IChannel> channels)
    {
        // Phase 1: validate every registration up-front. After this loop, the only
        // remaining failure mode is an id-already-in-use collision detected in Phase 2.
        // SlabSize is validated here so the Phase-3 INIT-frame write is infallible.
        int count = registrations.Length;
        var prepared = new PreparedRegistration[count];
        var seenKeys = new HashSet<ChannelRegistration>(count);
        bool enableReplay = options.MaxAutoReconnectAttempts != 0;
        var defaults = options.DefaultChannelOptions;

        for (int i = 0; i < count; i++)
        {
            var reg = registrations[i];
            string paramPath = $"{nameof(registrations)}[{i}]";

            if (reg.ChannelId is null)
                throw new ArgumentException($"{paramPath}.{nameof(ChannelRegistration.ChannelId)} is null.", nameof(registrations));

            byte[] idBytes = StreamMultiplexer.EncodeValidatedChannelId(reg.ChannelId, $"{paramPath}.{nameof(ChannelRegistration.ChannelId)}");

            if (!seenKeys.Add(reg))
                throw new ArgumentException(
                    $"Duplicate registration for channel id '{reg.ChannelId}' in direction {reg.Direction} at index {i}.",
                    nameof(registrations));

            ChannelOptions effectiveOptions;
            if (reg.Direction == ChannelDirection.Outbound)
            {
                if (reg.Options is not null)
                {
                    if (reg.Options.ChannelId != reg.ChannelId)
                    {
                        throw new ArgumentException(
                            $"{paramPath}: registration ChannelId '{reg.ChannelId}' does not match Options.ChannelId '{reg.Options.ChannelId}'.",
                            nameof(registrations));
                    }
                    StreamMultiplexer.ValidateSlabSize(reg.Options.SlabSize, $"{paramPath}.{nameof(ChannelRegistration.Options)}.{nameof(ChannelOptions.SlabSize)}");
                    effectiveOptions = reg.Options;
                }
                else
                {
                    effectiveOptions = new ChannelOptions
                    {
                        ChannelId = reg.ChannelId,
                        Priority = defaults.Priority,
                        SlabSize = defaults.SlabSize,
                        SendTimeout = defaults.SendTimeout,
                    };
                }
            }
            else
            {
                // Inbound: Options is not consulted; ReadChannel uses defaults today.
                effectiveOptions = new ChannelOptions
                {
                    ChannelId = reg.ChannelId,
                    Priority = defaults.Priority,
                    SlabSize = defaults.SlabSize,
                    SendTimeout = defaults.SendTimeout,
                };
            }

            prepared[i] = new PreparedRegistration(reg, idBytes, effectiveOptions);
        }

        // Phase 2: commit under AcceptLock so the batch is serialized against
        // single-channel AcceptChannel calls and against the reader thread's
        // INIT-arrival adoption.
        //
        // Outbound registrations require a vacant id; any collision (write,
        // read, or pending accept already present) rolls back every prior
        // commit from this same batch before returning false.
        //
        // Inbound registrations mirror the idempotent semantics of
        // AcceptChannel(string): an existing ReadChannel or pending accept
        // for the same id is reused. This is essential for composite transit
        // patterns where the peer's INIT for the inbound id may have arrived
        // before the local batch runs.
        var committedWrites = new List<(ushort Index, WriteChannel Channel)>(count);
        var committedPendingAccepts = new List<ReadChannel>(count);
        // Per-registration committed-channel handle, for Phase 3 assembly.
        var perRegChannel = new IChannel?[count];

        // Outer lock against StreamMultiplexer.ReassignPreHandshakeWriteChannelIndices
        // (#237). Phase 2's AllocateChannelIndex + RegisterWriteChannel and
        // Phase 3's WriteInitFrame must complete as one atomic unit against
        // the post-handshake reassign walk: otherwise the reassign snapshot
        // can miss a partially-published channel and the writer thread sends
        // an INIT with the pre-handshake wrong-parity index. Lock ordering is
        // ChannelIndexLock -> AcceptLock; ReassignPreHandshake takes only
        // ChannelIndexLock so the ordering cannot deadlock.
        lock (registry.ChannelIndexLock)
        {
        lock (registry.AcceptLock)
        {
            for (int i = 0; i < count; i++)
            {
                var p = prepared[i];
                string id = p.Reg.ChannelId;

                if (p.Reg.Direction == ChannelDirection.Outbound)
                {
                    if (registry.GetWriteChannelById(id) is not null ||
                        registry.GetReadChannelById(id) is not null ||
                        registry.GetPendingAcceptChannel(id) is not null)
                    {
                        RollbackPartialBatch(committedWrites, committedPendingAccepts);
                        channels = null!;
                        return false;
                    }

                    ushort idx = registry.AllocateChannelIndex();
                    var wc = new WriteChannel(
                        id,
                        idx,
                        p.EffectiveOptions.Priority,
                        p.EffectiveOptions.SlabSize,
                        p.EffectiveOptions.SendTimeout,
                        owner,
                        enableReplay);
                    try
                    {
                        registry.RegisterWriteChannel(idx, wc);
                    }
                    catch (MultiplexerException)
                    {
                        // Race with a concurrent single-channel OpenChannel (which does
                        // not take AcceptLock). Treat as collision.
                        RollbackPartialBatch(committedWrites, committedPendingAccepts);
                        channels = null!;
                        return false;
                    }
                    committedWrites.Add((idx, wc));
                    perRegChannel[i] = wc;
                }
                else
                {
                    // Inbound: idempotent — adopt existing ReadChannel or pending accept
                    // for the same id, otherwise create a new pending accept. A pre-existing
                    // outbound channel with the same id is still a collision (the id is
                    // bound to a write channel, not a read channel).
                    if (registry.GetWriteChannelById(id) is not null)
                    {
                        RollbackPartialBatch(committedWrites, committedPendingAccepts);
                        channels = null!;
                        return false;
                    }

                    var existing = registry.GetReadChannelById(id) ?? registry.GetPendingAcceptChannel(id);
                    if (existing is not null)
                    {
                        perRegChannel[i] = existing;
                    }
                    else
                    {
                        var rc = new ReadChannel(
                            id,
                            0, // index assigned later when remote INIT arrives
                            p.EffectiveOptions.Priority,
                            p.EffectiveOptions.SlabSize,
                            owner);
                        if (!registry.TryRegisterPendingAcceptChannel(id, rc))
                        {
                            RollbackPartialBatch(committedWrites, committedPendingAccepts);
                            channels = null!;
                            return false;
                        }
                        committedPendingAccepts.Add(rc);
                        perRegChannel[i] = rc;
                    }
                }
            }
        }

        // Phase 3: post-commit side effects. SlabSize validated in Phase 1 makes
        // WriteInitFrame infallible here, so no rollback can be necessary.
        // Only freshly-committed write channels emit an INIT frame and bump open
        // stats; reused inbound channels do nothing here.
        var result = new Dictionary<ChannelRegistration, IChannel>(count);
        int outboundCursor = 0;
        for (int i = 0; i < count; i++)
        {
            var p = prepared[i];
            var ch = perRegChannel[i]!;
            if (p.Reg.Direction == ChannelDirection.Outbound)
            {
                var wc = committedWrites[outboundCursor++].Channel;
                wc.WriteInitFrame(p.IdBytes);
                if (isConnected) wc.MarkConnected();
                Interlocked.Increment(ref stats._openChannels);
                Interlocked.Increment(ref stats._totalChannelsOpened);
            }
            else if (committedPendingAccepts.Contains((ReadChannel)ch))
            {
                // Freshly-committed pending accept: mark connected just like AcceptChannel does.
                if (isConnected) ((ReadChannel)ch).MarkConnected();
            }
            // else: reused existing inbound channel — no side effects.

            result[p.Reg] = ch;
        }

        channels = result;
        return true;
        }
    }

    private void RollbackPartialBatch(
        List<(ushort Index, WriteChannel Channel)> committedWrites,
        List<ReadChannel> committedPendingAccepts)
    {
        // Caller holds AcceptLock. Unregister in reverse insertion order; channel
        // indices are intentionally not reclaimed (allocation is monotonic).
        for (int i = committedWrites.Count - 1; i >= 0; i--)
        {
            var (idx, ch) = committedWrites[i];
            registry.UnregisterChannel(idx, ch.ChannelId);
        }
        for (int i = committedPendingAccepts.Count - 1; i >= 0; i--)
        {
            registry.RemovePendingAcceptChannel(committedPendingAccepts[i].ChannelId);
        }
    }

    private readonly record struct PreparedRegistration(
        ChannelRegistration Reg,
        byte[] IdBytes,
        ChannelOptions EffectiveOptions);
}
