using NetConduit.Enums;

namespace NetConduit.Models;

/// <summary>
/// Describes a single channel to be registered as part of an atomic group via
/// <see cref="Interfaces.IStreamMultiplexer.TryRegisterChannels"/>.
/// <para>
/// Value equality is based on <see cref="ChannelId"/> and <see cref="Direction"/>
/// only — <see cref="Options"/> is excluded. Two registrations that target the
/// same channel id in the same direction compare equal, which is the dict-key
/// contract used by the result of <c>TryRegisterChannels</c>.
/// </para>
/// </summary>
/// <param name="ChannelId">The string identifier for the channel.</param>
/// <param name="Direction">Whether the channel is opened (outbound) or accepted (inbound) locally.</param>
public readonly record struct ChannelRegistration(string ChannelId, ChannelDirection Direction)
{
    /// <summary>
    /// Per-channel options. Only consulted for <see cref="ChannelDirection.Outbound"/>
    /// registrations. When <c>null</c>, the multiplexer's default channel options apply.
    /// Excluded from value equality so the registration can be used directly as a
    /// dictionary key without options-instance fragility.
    /// </summary>
    public ChannelOptions? Options { get; init; }
}
