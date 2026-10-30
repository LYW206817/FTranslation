using FastTranslation.Core;
using Xunit;

namespace FastTranslation.Core.Tests;

public sealed class GatewayHealthTests
{
    [Fact]
    public void Starts_unavailable_so_the_first_translation_never_waits_for_a_probe()
    {
        Assert.False(new GatewayHealth().IsUsable);
    }

    [Fact]
    public void A_reachable_probe_enables_the_gateway_until_a_failure_marks_it_down()
    {
        var health = new GatewayHealth();

        health.MarkProbed(true);
        Assert.True(health.IsUsable);

        health.MarkDown();
        Assert.False(health.IsUsable);
    }

    [Fact]
    public void A_failed_probe_after_a_healthy_period_marks_it_down_again()
    {
        var health = new GatewayHealth();
        health.MarkProbed(true);

        health.MarkProbed(false);

        Assert.False(health.IsUsable);
    }

    [Fact]
    public void Only_one_probe_runs_at_a_time()
    {
        var health = new GatewayHealth();

        Assert.True(health.TryBeginProbe());
        Assert.False(health.TryBeginProbe());

        health.EndProbe();
        Assert.True(health.TryBeginProbe());
    }
}
