namespace Munibot.Tests;

public sealed class ReconnectDelayPolicyTests
{
    [Fact]
    public void NetworkTimeoutSequenceDoublesAndStopsAtConfiguredCap()
    {
        var policy = new ReconnectDelayPolicy(
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(300));

        var delays = Enumerable.Range(1, 8)
            .Select(index => policy.GetDelay(Disconnect(SessionDisconnectKind.NetworkTimeout, index)).Delay.TotalSeconds)
            .ToArray();

        Assert.Equal(new double[] { 15, 30, 60, 120, 240, 300, 300, 300 }, delays);
    }

    [Fact]
    public void SuccessfulReconnectDoesNotResetTimeoutStreak()
    {
        var policy = new ReconnectDelayPolicy(
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(300));
        var firstDisconnect = Disconnect(SessionDisconnectKind.NetworkTimeout, 1);

        Assert.Equal(TimeSpan.FromSeconds(15), policy.GetDelay(firstDisconnect).Delay);
        Assert.Equal(TimeSpan.FromSeconds(15), policy.GetDelay(firstDisconnect).Delay);
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            policy.GetDelay(Disconnect(SessionDisconnectKind.NetworkTimeout, 2)).Delay);
    }

    [Fact]
    public void StableConnectionResetRestartsTimeoutSequence()
    {
        var policy = new ReconnectDelayPolicy(
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(300));
        policy.GetDelay(Disconnect(SessionDisconnectKind.NetworkTimeout, 1));
        policy.GetDelay(Disconnect(SessionDisconnectKind.NetworkTimeout, 2));

        policy.ResetTimeoutStreak();

        Assert.Equal(
            TimeSpan.FromSeconds(15),
            policy.GetDelay(Disconnect(SessionDisconnectKind.NetworkTimeout, 3)).Delay);
    }

    [Fact]
    public void NonTimeoutDisconnectUsesBaseDelayAndBreaksTimeoutSequence()
    {
        var policy = new ReconnectDelayPolicy(
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(300));
        policy.GetDelay(Disconnect(SessionDisconnectKind.NetworkTimeout, 1));
        policy.GetDelay(Disconnect(SessionDisconnectKind.NetworkTimeout, 2));

        var nonTimeout = policy.GetDelay(Disconnect(SessionDisconnectKind.Other, 3));
        var nextTimeout = policy.GetDelay(Disconnect(SessionDisconnectKind.NetworkTimeout, 4));

        Assert.Equal(TimeSpan.FromSeconds(15), nonTimeout.Delay);
        Assert.Equal(0, nonTimeout.NetworkTimeoutStreak);
        Assert.Equal(TimeSpan.FromSeconds(15), nextTimeout.Delay);
    }

    [Fact]
    public void MaximumBelowLegacyBaseDoesNotShortenConfiguredDelay()
    {
        var policy = new ReconnectDelayPolicy(
            TimeSpan.FromSeconds(600),
            TimeSpan.FromSeconds(300));

        var delay = policy.GetDelay(Disconnect(SessionDisconnectKind.NetworkTimeout, 1));

        Assert.Equal(TimeSpan.FromSeconds(600), delay.Delay);
    }

    private static SessionDisconnectSnapshot Disconnect(SessionDisconnectKind kind, int seconds)
        => new(kind, DateTimeOffset.UnixEpoch.AddSeconds(seconds), null);
}
