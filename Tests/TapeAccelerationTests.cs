using System;
using TradingRadar.Engine;
using Xunit;

public class TapeAccelerationTests
{
    static DateTime T(int ms) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);

    // Rising aggressor net rate (buyers accelerating) => positive acceleration.
    [Fact]
    public void Acceleration_is_positive_when_net_rate_is_rising()
    {
        var ta = new TapeAcceleration(0.1);
        // netRate climbs +50 every 100 ms => a steady +500 / s derivative.
        for (int i = 0; i < 40; i++) ta.Sample(i * 50.0, T(i * 100));
        Assert.True(ta.Ready);
        Assert.True(ta.Acceleration > 0.0);
    }

    // Falling aggressor net rate (sellers accelerating) => negative acceleration.
    [Fact]
    public void Acceleration_is_negative_when_net_rate_is_falling()
    {
        var ta = new TapeAcceleration(0.1);
        // netRate drops -50 every 100 ms => a steady -500 / s derivative.
        for (int i = 0; i < 40; i++) ta.Sample(-i * 50.0, T(i * 100));
        Assert.True(ta.Ready);
        Assert.True(ta.Acceleration < 0.0);
    }

    // Constant net rate => zero frame-to-frame derivative => EWMA stays ~0.
    [Fact]
    public void Acceleration_is_near_zero_when_net_rate_is_flat()
    {
        var ta = new TapeAcceleration(0.1);
        for (int i = 0; i < 40; i++) ta.Sample(120.0, T(i * 100));
        Assert.True(ta.Ready);
        Assert.True(Math.Abs(ta.Acceleration) < 1e-6);
    }

    // Ready gate mirrors TapeSpeed: not Ready until MinSamples (20) samples have arrived.
    [Fact]
    public void Not_ready_before_MinSamples_then_ready_on_the_20th_sample()
    {
        var ta = new TapeAcceleration(0.1);
        for (int i = 0; i < 19; i++) ta.Sample(i * 10.0, T(i * 100)); // 19 samples
        Assert.False(ta.Ready);            // not yet warmed up
        ta.Sample(190.0, T(1900));         // 20th sample -> Ready flips true this call
        Assert.True(ta.Ready);
    }
}
