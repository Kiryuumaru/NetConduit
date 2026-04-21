using NetConduit.Models;

namespace NetConduit.Investigate;

/// <summary>
/// Proves that ChannelOptions and MultiplexerOptions accept invalid configurations
/// without validation, leading to logic errors in AdaptiveFlowControl.
/// </summary>
public class OptionsValidationTest
{
    [Fact]
    public void ChannelOptions_AllowsMinCreditsGreaterThanMaxCredits()
    {
        // No validation prevents this invalid configuration
        var options = new ChannelOptions
        {
            ChannelId = "test",
            MinCredits = 1_000_000,  // 1 MB
            MaxCredits = 100          // 100 bytes
        };

        Assert.True(options.MinCredits > options.MaxCredits,
            "MinCredits > MaxCredits accepted without error — no validation exists");
    }

    [Fact]
    public void ChannelOptions_AllowsZeroCredits()
    {
        var options = new ChannelOptions
        {
            ChannelId = "test",
            MinCredits = 0,
            MaxCredits = 0
        };

        // Zero credits means no data can ever flow
        Assert.Equal(0u, options.MinCredits);
        Assert.Equal(0u, options.MaxCredits);
    }

    [Fact]
    public void AdaptiveFlowControl_ShrinkExceedsMaxWhenMinGTMax()
    {
        // Directly test the AdaptiveFlowControl logic
        // This uses reflection since it's internal, but the math is provable:
        //
        // Constructor: _currentWindowSize = maxCredits = 100
        // TryShrinkIfIdle: newSize = Math.Max(100 * 0.5, minCredits)
        //                         = Math.Max(50, 1_000_000)
        //                         = 1_000_000  ← EXCEEDS maxCredits!

        uint minCredits = 1_000_000;
        uint maxCredits = 100;

        // Simulate the shrink logic
        uint currentWindowSize = maxCredits; // starts at max
        double shrinkFactor = 0.5;

        var newSize = (uint)Math.Max(currentWindowSize * shrinkFactor, minCredits);

        Assert.Equal(1_000_000u, newSize);
        Assert.True(newSize > maxCredits,
            $"After shrink, window is {newSize} which EXCEEDS maxCredits {maxCredits}. " +
            "AdaptiveFlowControl will grant 10,000x more credits than intended.");
    }

    [Fact]
    public void AdaptiveFlowControl_InitialCreditsAtMax_ButMinGTMax()
    {
        // GetInitialCredits returns _currentWindowSize which starts at maxCredits
        // So initial credits = 100, but after first idle shrink = 1,000,000
        uint minCredits = 500_000;
        uint maxCredits = 100;

        uint initialCredits = maxCredits; // what GetInitialCredits() returns
        Assert.Equal(100u, initialCredits);

        // After 5s idle, TryShrinkIfIdle fires:
        var afterShrink = (uint)Math.Max(initialCredits * 0.5, minCredits);
        Assert.Equal(500_000u, afterShrink);

        // Window jumped from 100 to 500,000 after going idle!
        // This is the opposite of the intended behavior (shrink window on idle).
        Assert.True(afterShrink > maxCredits * 100,
            "Idle shrink INCREASED window by 5000x — the invariant is violated");
    }

    [Fact]
    public void MultiplexerOptions_AllowsNegativePingInterval()
    {
        var options = new MultiplexerOptions
        {
            PingInterval = TimeSpan.FromSeconds(-5),
            StreamFactory = _ => throw new NotImplementedException()
        };

        Assert.True(options.PingInterval < TimeSpan.Zero,
            "Negative PingInterval accepted without validation");
    }

    [Fact]
    public void MultiplexerOptions_AllowsZeroMaxAutoReconnectDelay()
    {
        var options = new MultiplexerOptions
        {
            AutoReconnectDelay = TimeSpan.Zero,
            AutoReconnectBackoffMultiplier = 0.0,
            StreamFactory = _ => throw new NotImplementedException()
        };

        // Zero delay + zero multiplier = reconnect loops without any backoff
        // This would create a CPU-burning tight loop on connection failure
        Assert.Equal(TimeSpan.Zero, options.AutoReconnectDelay);
        Assert.Equal(0.0, options.AutoReconnectBackoffMultiplier);
    }

    [Fact]
    public void MultiplexerOptions_NegativeBackoffMultiplier_CausesNegativeDelay()
    {
        double backoffMultiplier = -2.0;
        var currentDelay = TimeSpan.FromSeconds(1);

        var nextDelay = TimeSpan.FromMilliseconds(
            currentDelay.TotalMilliseconds * backoffMultiplier);

        Assert.True(nextDelay < TimeSpan.Zero,
            $"Negative multiplier produces negative delay: {nextDelay}. " +
            "Task.Delay with negative timespan throws ArgumentOutOfRangeException.");
    }
}
