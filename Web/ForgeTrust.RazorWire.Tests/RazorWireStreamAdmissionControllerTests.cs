using ForgeTrust.RazorWire.Streams;

namespace ForgeTrust.RazorWire.Tests;

public sealed class RazorWireStreamAdmissionControllerTests
{
    [Fact]
    public void TryAcquire_ReleasesCapacityWhenLeaseIsDisposed()
    {
        var controller = CreateController(maxLiveSubscriptions: 1);
        var first = controller.TryAcquire("orders");

        Assert.True(first.Accepted);
        Assert.Equal(1, controller.Snapshot.LiveSubscriptions);

        first.Lease!.Dispose();

        Assert.Equal(0, controller.Snapshot.LiveSubscriptions);
        Assert.Equal(0, controller.Snapshot.LiveChannels);
        Assert.True(controller.TryAcquire("orders").Accepted);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var controller = CreateController(maxLiveSubscriptions: 1);
        var result = controller.TryAcquire("orders");

        result.Lease!.Dispose();
        result.Lease.Dispose();

        Assert.Equal(0, controller.Snapshot.LiveSubscriptions);
        Assert.Equal(0, controller.Snapshot.LiveChannels);
    }

    [Fact]
    public void TryAcquire_RejectsOverlongChannelWithoutCreatingCounters()
    {
        var controller = CreateController(maxChannelNameLength: 3);

        var result = controller.TryAcquire("orders");

        Assert.False(result.Accepted);
        Assert.Equal(RazorWireStreamAdmissionRejectionReason.ChannelNameTooLong, result.RejectionReason);
        Assert.Equal(0, controller.Snapshot.LiveSubscriptions);
        Assert.Equal(0, controller.Snapshot.LiveChannels);
    }

    [Fact]
    public void TryAcquire_RejectsInvalidChannelCharacters()
    {
        var controller = CreateController();

        var result = controller.TryAcquire("orders/1");

        Assert.False(result.Accepted);
        Assert.Equal(RazorWireStreamAdmissionRejectionReason.InvalidChannelName, result.RejectionReason);
    }

    [Fact]
    public void TryAcquire_EnforcesMaxLiveSubscriptionsBeforeChannelLimits()
    {
        var controller = CreateController(
            maxLiveChannels: 1,
            maxLiveSubscriptions: 1,
            maxLiveSubscriptionsPerChannel: 1);

        var accepted = controller.TryAcquire("orders");
        var rejected = controller.TryAcquire("billing");

        Assert.True(accepted.Accepted);
        Assert.False(rejected.Accepted);
        Assert.Equal(RazorWireStreamAdmissionRejectionReason.TooManyLiveSubscriptions, rejected.RejectionReason);
    }

    [Fact]
    public void TryAcquire_EnforcesPerChannelLimitForExistingChannel()
    {
        var controller = CreateController(maxLiveSubscriptions: 4, maxLiveSubscriptionsPerChannel: 1);

        Assert.True(controller.TryAcquire("orders").Accepted);
        var rejected = controller.TryAcquire("orders");

        Assert.False(rejected.Accepted);
        Assert.Equal(RazorWireStreamAdmissionRejectionReason.TooManyLiveSubscriptionsPerChannel, rejected.RejectionReason);
        Assert.Equal(1, controller.Snapshot.LiveSubscriptions);
        Assert.Equal(1, controller.Snapshot.LiveChannels);
    }

    [Fact]
    public void TryAcquire_EnforcesChannelCardinalityForNewChannels()
    {
        var controller = CreateController(maxLiveChannels: 1, maxLiveSubscriptions: 4);

        Assert.True(controller.TryAcquire("orders").Accepted);
        var rejected = controller.TryAcquire("billing");

        Assert.False(rejected.Accepted);
        Assert.Equal(RazorWireStreamAdmissionRejectionReason.TooManyLiveChannels, rejected.RejectionReason);
        Assert.Equal(1, controller.Snapshot.LiveSubscriptions);
        Assert.Equal(1, controller.Snapshot.LiveChannels);
    }

    [Fact]
    public void TryAcquire_SameChannelConsumesOneLiveChannelSlot()
    {
        var controller = CreateController(maxLiveChannels: 1, maxLiveSubscriptions: 3, maxLiveSubscriptionsPerChannel: 3);

        var first = controller.TryAcquire("orders");
        var second = controller.TryAcquire("orders");

        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        Assert.Equal(2, controller.Snapshot.LiveSubscriptions);
        Assert.Equal(1, controller.Snapshot.LiveChannels);

        first.Lease!.Dispose();
        Assert.Equal(1, controller.Snapshot.LiveChannels);

        second.Lease!.Dispose();
        Assert.Equal(0, controller.Snapshot.LiveChannels);
    }

    [Fact]
    public async Task TryAcquire_ConcurrentAcquisitionsDoNotOversubscribe()
    {
        var controller = CreateController(maxLiveSubscriptions: 8, maxLiveSubscriptionsPerChannel: 8);
        var results = await Task.WhenAll(
            Enumerable.Range(0, 64)
                .Select(_ => Task.Run(() => controller.TryAcquire("orders"))));

        Assert.Equal(8, results.Count(result => result.Accepted));
        Assert.Equal(56, results.Count(result =>
            result.RejectionReason == RazorWireStreamAdmissionRejectionReason.TooManyLiveSubscriptions));
        Assert.Equal(8, controller.Snapshot.LiveSubscriptions);

        foreach (var result in results.Where(result => result.Accepted))
        {
            result.Lease!.Dispose();
        }

        Assert.Equal(0, controller.Snapshot.LiveSubscriptions);
        Assert.Equal(0, controller.Snapshot.LiveChannels);
    }

    private static RazorWireStreamAdmissionController CreateController(
        int maxChannelNameLength = RazorWireStreamOptions.DefaultMaxChannelNameLength,
        int maxLiveChannels = RazorWireStreamOptions.DefaultMaxLiveChannels,
        int maxLiveSubscriptions = RazorWireStreamOptions.DefaultMaxLiveSubscriptions,
        int maxLiveSubscriptionsPerChannel = RazorWireStreamOptions.DefaultMaxLiveSubscriptionsPerChannel)
    {
        var options = new RazorWireOptions();
        options.Streams.MaxChannelNameLength = maxChannelNameLength;
        options.Streams.MaxLiveChannels = maxLiveChannels;
        options.Streams.MaxLiveSubscriptions = maxLiveSubscriptions;
        options.Streams.MaxLiveSubscriptionsPerChannel = maxLiveSubscriptionsPerChannel;

        return new RazorWireStreamAdmissionController(options);
    }
}
