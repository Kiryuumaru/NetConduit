using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using NetConduit.Transport.Udp;

namespace NetConduit.Transport.Udp.IntegrationTests;

public class ReliableUdpStreamStaleAckTests
{
    private const byte FlagData = 0x01;
    private const byte FlagAck = 0x02;

    /// <summary>
    /// Regression for #302: a delayed/duplicated ACK for a previous sequence number must not
    /// complete the pending-ACK TCS for the current send. Pre-fix, a stale ACK completed the
    /// TCS with the wrong seq, the send loop saw acked != _sendSeq, retransmitted, then
    /// re-awaited the already-completed TCS — spinning into an infinite tight resend loop.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task SendWithAckAsync_StaleAckForPreviousSeq_DoesNotSpinInfiniteResend()
    {
        using var rogue = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var roguePort = ((IPEndPoint)rogue.Client.LocalEndPoint!).Port;

        var sender = new UdpClient(AddressFamily.InterNetwork);
        sender.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        sender.Connect(IPAddress.Loopback, roguePort);
        var senderPort = ((IPEndPoint)sender.Client.LocalEndPoint!).Port;
        rogue.Connect(IPAddress.Loopback, senderPort);

        await using var stream = new ReliableUdpStream(sender, new ReliableUdpOptions
        {
            RetransmitTimeout = TimeSpan.FromMilliseconds(200),
            MaxRetransmits = 50
        });

        // Step 1: write byte A → DATA seq=1. ACK(1) completes the first send.
        var write1 = stream.WriteAsync(new byte[] { 0xAA }).AsTask();
        var dgm1 = await rogue.ReceiveAsync();
        Assert.Equal(FlagData, dgm1.Buffer[0]);
        Assert.Equal((uint)1, BinaryPrimitives.ReadUInt32BigEndian(dgm1.Buffer.AsSpan(1, 4)));
        await SendAck(rogue, seq: 1);
        await write1.WaitAsync(TimeSpan.FromSeconds(2));

        // Step 2: write byte B → DATA seq=2. After observing the new DATA (which proves the
        // new pending-ACK TCS is in place), inject a STALE ACK(1) before the legitimate ACK(2).
        var write2 = stream.WriteAsync(new byte[] { 0xBB }).AsTask();
        var dgm2 = await rogue.ReceiveAsync();
        Assert.Equal(FlagData, dgm2.Buffer[0]);
        Assert.Equal((uint)2, BinaryPrimitives.ReadUInt32BigEndian(dgm2.Buffer.AsSpan(1, 4)));

        // Pre-fix: stale ACK(1) completes the TCS for seq=2 with value 1; the send loop sees
        // acked=1 != _sendSeq=2, retransmits, re-awaits the already-completed TCS, returns
        // immediately, retransmits again — infinite spin until outer cancellation.
        // Post-fix: receive loop filters seq != _sendSeq, so stale ACK(1) is dropped.
        await SendAck(rogue, seq: 1);

        // Brief delay so the stale ACK is delivered before the legitimate one.
        await Task.Delay(50);

        await SendAck(rogue, seq: 2);

        // WaitAsync(5s) throws TimeoutException pre-fix, completes promptly post-fix.
        await write2.WaitAsync(TimeSpan.FromSeconds(5));

        // Confirm no retransmit storm: rogue should not see a pile of duplicate DATA seq=2.
        int extras = 0;
        using var drainCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            while (true)
            {
                _ = await rogue.ReceiveAsync(drainCts.Token);
                extras++;
            }
        }
        catch (OperationCanceledException) { }

        Assert.True(extras < 5, $"Expected no retransmit storm; saw {extras} extra datagrams.");
    }

    private static Task SendAck(UdpClient client, uint seq)
    {
        var buf = new byte[7];
        buf[0] = FlagAck;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(1, 4), seq);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(5, 2), 0);
        return client.SendAsync(buf, buf.Length);
    }
}
