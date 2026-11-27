using FastTranslation.Server.Gateway;
using Xunit;

namespace FastTranslation.Server.Tests;

public class GatewayUserCapacityTests
{
    [Fact]
    public void TryEnter_allows_the_limit_and_rejects_the_next_call()
    {
        var capacity = new GatewayUserCapacity();
        var user = Guid.NewGuid();

        Assert.True(capacity.TryEnter(user, 2));
        Assert.True(capacity.TryEnter(user, 2));
        Assert.False(capacity.TryEnter(user, 2));
    }

    [Fact]
    public void Exit_frees_a_slot_and_the_count_drops_when_it_hits_zero()
    {
        var capacity = new GatewayUserCapacity();
        var user = Guid.NewGuid();

        Assert.True(capacity.TryEnter(user, 1));
        Assert.False(capacity.TryEnter(user, 1));

        capacity.Exit(user);

        Assert.True(capacity.TryEnter(user, 1));
    }

    [Fact]
    public void Limits_are_per_user()
    {
        var capacity = new GatewayUserCapacity();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.True(capacity.TryEnter(first, 1));
        Assert.False(capacity.TryEnter(first, 1));
        Assert.True(capacity.TryEnter(second, 1));
    }

    [Fact]
    public void Non_positive_limit_is_unlimited()
    {
        var capacity = new GatewayUserCapacity();
        var user = Guid.NewGuid();

        Assert.True(capacity.TryEnter(user, 0));
        Assert.True(capacity.TryEnter(user, -1));
    }
}
