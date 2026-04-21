using NetConduit.Investigate.Helpers;
using NetConduit.Models;
using NetConduit.Transits;

namespace NetConduit.Investigate;

/// <summary>
/// Proves that channel IDs containing ">>" or "<<" produce ambiguous
/// multi-suffixed channel names that break transit conventions.
/// </summary>
public class ChannelSuffixCollisionTest
{
    [Theory]
    [InlineData("data>>", "data>>>>", "data>><<")]
    [InlineData("data<<", "data<<>>", "data<<<<")]
    [InlineData(">>", ">>>>", ">><<")]
    [InlineData("<<", "<<>>", "<<<<")]
    [InlineData("chat>>room", "chat>>room>>", "chat>>room<<")]
    public void SuffixedChannelId_ProducesAmbiguousNames(
        string channelId, string expectedWrite, string expectedRead)
    {
        var writeId = channelId + TransitExtensions.OutboundSuffix;   // ">>"
        var readId = channelId + TransitExtensions.InboundSuffix;     // "<<"

        Assert.Equal(expectedWrite, writeId);
        Assert.Equal(expectedRead, readId);

        // These names are syntactically valid but semantically ambiguous.
        // "data>>>>" could be confused with a base ID of "data>>" + suffix ">>"
        // or a base ID of "data" + double suffix ">>>>".
        // No validation prevents these collisions.
    }

    [Fact]
    public async Task DuplexTransit_WithSuffixedChannelId_CreatesAmbiguousChannels()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var pipe = new DuplexPipe();
        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, ct: cts.Token);

        try
        {
            // Open a normal transit for "data"
            var normalWrite = await mux1.OpenChannelAsync(
                new ChannelOptions { ChannelId = "data>>" }, cts.Token);
            var normalRead = await mux2.AcceptChannelAsync("data>>", cts.Token);

            // Now try to open a DUPLEX transit for "data" —
            // this tries to create write channel "data>>" which ALREADY EXISTS
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await mux1.OpenDuplexStreamAsync("data", cts.Token);
            });

            // This proves the suffix convention collides with user-chosen channel names.
            // If a user opens channel "data>>" directly, then later tries to use
            // OpenDuplexStreamAsync("data"), the ">>" suffix produces "data>>" which conflicts.

            await normalWrite.DisposeAsync();
            await normalRead.DisposeAsync();
        }
        finally
        {
            await mux1.DisposeAsync();
            await mux2.DisposeAsync();
        }
    }

    [Fact]
    public async Task DoubleNesting_CreatesFourCharacterSuffix()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var pipe = new DuplexPipe();
        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, ct: cts.Token);

        try
        {
            // Use a channel ID that already ends with ">>"
            var channelId = "stream>>";

            // OpenDuplexStreamAsync will create:
            //   write: "stream>>>>"  (4 arrows)
            //   read:  "stream>><<"  (mixed arrows)
            var writeChannelId = channelId + TransitExtensions.OutboundSuffix;
            var readChannelId = channelId + TransitExtensions.InboundSuffix;

            Assert.Equal("stream>>>>", writeChannelId);
            Assert.Equal("stream>><<", readChannelId);

            // Open the channels to prove they work but create confusing names
            var writeChannel = await mux1.OpenChannelAsync(
                new ChannelOptions { ChannelId = writeChannelId }, cts.Token);
            var readChannel = await mux2.AcceptChannelAsync(writeChannelId, cts.Token);

            Assert.Equal("stream>>>>", writeChannel.ChannelId);
            Assert.Equal("stream>>>>", readChannel.ChannelId);

            // The channel IDs are valid but deeply confusing and collision-prone
            await writeChannel.DisposeAsync();
            await readChannel.DisposeAsync();
        }
        finally
        {
            await mux1.DisposeAsync();
            await mux2.DisposeAsync();
        }
    }
}
