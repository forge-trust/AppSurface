using System.Threading.Channels;
using ForgeTrust.RazorWire.Bridge;
using ForgeTrust.RazorWire.Streams;

namespace ForgeTrust.RazorWire.Tests;

public class InMemoryRazorWireStreamHubTests
{
    [Fact]
    public async Task PublishAsync_DeliversMessagesToAllSubscribers()
    {
        // Arrange
        var hub = new InMemoryRazorWireStreamHub();
        var first = hub.Subscribe("orders");
        var second = hub.Subscribe("orders");

        // Act
        await hub.PublishAsync("orders", "created");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var firstMessage = await first.ReadAsync(cts.Token);
        var secondMessage = await second.ReadAsync(cts.Token);

        // Assert
        Assert.Equal("created", firstMessage);
        Assert.Equal("created", secondMessage);
    }

    [Fact]
    public async Task Unsubscribe_CompletesReaderAndStopsDelivery()
    {
        // Arrange
        var hub = new InMemoryRazorWireStreamHub();
        var reader = hub.Subscribe("orders");

        // Act
        hub.Unsubscribe("orders", reader);
        await hub.PublishAsync("orders", "ignored");

        // Assert
        var waitTask = reader.WaitToReadAsync().AsTask();
        var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(waitTask, completed);
        Assert.False(await waitTask);
    }

    [Fact]
    public void Unsubscribe_RemovesEmptyLiveChannelState_ForManyUniqueChannels()
    {
        // Arrange
        var hub = new InMemoryRazorWireStreamHub();

        // Act
        for (var index = 0; index < 300; index++)
        {
            var channel = $"orders-{index}";
            var reader = hub.Subscribe(channel);
            hub.Unsubscribe(channel, reader);
        }

        // Assert
        var diagnostics = hub.GetDiagnostics();
        Assert.Equal(0, diagnostics.LiveChannelCount);
        Assert.Equal(0, diagnostics.LiveSubscriberCount);
        Assert.Equal(0, diagnostics.SubscriptionCount);
    }

    [Fact]
    public async Task Unsubscribe_WithWrongChannel_RemovesRecordedSubscription()
    {
        // Arrange
        var hub = new InMemoryRazorWireStreamHub();
        var reader = hub.Subscribe("orders");

        // Act
        hub.Unsubscribe("not-orders", reader);
        await hub.PublishAsync("orders", "ignored");

        // Assert
        var waitTask = reader.WaitToReadAsync().AsTask();
        var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(waitTask, completed);
        Assert.False(await waitTask);

        var diagnostics = hub.GetDiagnostics();
        Assert.Equal(0, diagnostics.LiveChannelCount);
        Assert.Equal(0, diagnostics.LiveSubscriberCount);
        Assert.Equal(0, diagnostics.SubscriptionCount);
    }

    [Fact]
    public async Task PublishAsync_PrunesClosedWriterAndRemovesEmptyLiveChannel()
    {
        // Arrange
        var hub = new InMemoryRazorWireStreamHub();
        var reader = hub.Subscribe("orders");
        Assert.True(hub.TryCompleteSubscriberWriterForTesting(reader));

        // Act
        await hub.PublishAsync("orders", "ignored");

        // Assert
        var waitTask = reader.WaitToReadAsync().AsTask();
        var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(waitTask, completed);
        Assert.False(await waitTask);

        var diagnostics = hub.GetDiagnostics();
        Assert.Equal(0, diagnostics.LiveChannelCount);
        Assert.Equal(0, diagnostics.LiveSubscriberCount);
        Assert.Equal(0, diagnostics.SubscriptionCount);
    }

    [Fact]
    public async Task PublishAsync_WithNoSubscribers_CompletesWithoutErrors()
    {
        // Arrange
        var hub = new InMemoryRazorWireStreamHub();

        // Act + Assert
        await hub.PublishAsync("missing", "payload");
    }

    [Fact]
    public async Task Subscribe_WithReplay_DeliversRetainedMessagesBeforeLiveMessages()
    {
        var hub = new InMemoryRazorWireStreamHub();
        await hub.PublishAsync("orders", "first", new RazorWireStreamPublishOptions { Replay = true });
        await hub.PublishAsync("orders", "live-only");
        await hub.PublishAsync("orders", "second", new RazorWireStreamPublishOptions { Replay = true });

        var reader = hub.Subscribe("orders", new RazorWireStreamSubscribeOptions { Replay = true });
        await hub.PublishAsync("orders", "third");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Assert.Equal("first", await reader.ReadAsync(cts.Token));
        Assert.Equal("second", await reader.ReadAsync(cts.Token));
        Assert.Equal("third", await reader.ReadAsync(cts.Token));
    }

    [Fact]
    public async Task PublishAsync_WithBuilderOutput_DeliversEncodedStreamThroughLiveAndReplay()
    {
        var hub = new InMemoryRazorWireStreamHub();
        var liveReader = hub.Subscribe("orders");
        var stream = new RazorWireStreamBuilder()
            .Update("<target&name>", "<script>alert(1)</script>")
            .Build();

        await hub.PublishAsync("orders", stream, new RazorWireStreamPublishOptions { Replay = true });
        var replayReader = hub.Subscribe("orders", new RazorWireStreamSubscribeOptions { Replay = true });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var liveMessage = await liveReader.ReadAsync(cts.Token);
        var replayMessage = await replayReader.ReadAsync(cts.Token);

        Assert.Equal(liveMessage, replayMessage);
        Assert.Contains("target=\"&lt;target&amp;name&gt;\"", liveMessage);
        Assert.Contains("<template>&lt;script&gt;alert(1)&lt;/script&gt;</template>", liveMessage);
        Assert.DoesNotContain("<script>alert(1)</script>", liveMessage);
    }

    [Fact]
    public async Task Subscribe_WithoutReplay_RemainsLiveOnly()
    {
        var hub = new InMemoryRazorWireStreamHub();
        await hub.PublishAsync("orders", "retained", new RazorWireStreamPublishOptions { Replay = true });

        var reader = hub.Subscribe("orders");
        await hub.PublishAsync("orders", "live");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Assert.Equal("live", await reader.ReadAsync(cts.Token));
    }

    [Fact]
    public void Subscribe_WithReplayAcrossUniqueEmptyChannels_DoesNotRetainReplayState()
    {
        // Arrange
        var hub = new InMemoryRazorWireStreamHub();

        // Act
        for (var index = 0; index < 300; index++)
        {
            var channel = $"orders-{index}";
            var reader = hub.Subscribe(channel, new RazorWireStreamSubscribeOptions { Replay = true });
            hub.Unsubscribe(channel, reader);
        }

        // Assert
        var diagnostics = hub.GetDiagnostics();
        Assert.Equal(0, diagnostics.LiveChannelCount);
        Assert.Equal(0, diagnostics.LiveSubscriberCount);
        Assert.Equal(0, diagnostics.SubscriptionCount);
        Assert.Equal(0, diagnostics.ReplayChannelCount);
    }

    [Fact]
    public async Task PublishAsync_WithReplay_PrunesOldInactiveReplayChannels()
    {
        var hub = new InMemoryRazorWireStreamHub();
        for (var index = 0; index < 260; index++)
        {
            await hub.PublishAsync(
                $"orders-{index}",
                $"retained-{index}",
                new RazorWireStreamPublishOptions { Replay = true });
        }

        var prunedReader = hub.Subscribe("orders-0", new RazorWireStreamSubscribeOptions { Replay = true });
        await hub.PublishAsync("orders-0", "live");
        var retainedReader = hub.Subscribe("orders-259", new RazorWireStreamSubscribeOptions { Replay = true });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Assert.Equal("live", await prunedReader.ReadAsync(cts.Token));
        Assert.Equal("retained-259", await retainedReader.ReadAsync(cts.Token));
    }

    [Fact]
    public async Task PublishAsync_WithReplay_PrunesInactiveChannels_WhenOldestChannelsAreActive()
    {
        var hub = new InMemoryRazorWireStreamHub();
        var activeReaders = new List<ChannelReader<string>>();
        for (var index = 0; index < 20; index++)
        {
            await hub.PublishAsync(
                $"orders-{index}",
                $"retained-{index}",
                new RazorWireStreamPublishOptions { Replay = true });
            activeReaders.Add(hub.Subscribe($"orders-{index}", new RazorWireStreamSubscribeOptions { Replay = true }));
        }

        for (var index = 20; index < 270; index++)
        {
            await hub.PublishAsync(
                $"orders-{index}",
                $"retained-{index}",
                new RazorWireStreamPublishOptions { Replay = true });
        }

        var diagnostics = hub.GetDiagnostics();
        Assert.Equal(256, diagnostics.ReplayChannelCount);

        var activeReplayReader = hub.Subscribe("orders-0", new RazorWireStreamSubscribeOptions { Replay = true });
        var inactivePrunedReader = hub.Subscribe("orders-20", new RazorWireStreamSubscribeOptions { Replay = true });
        await hub.PublishAsync("orders-20", "live");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Assert.Equal("retained-0", await activeReplayReader.ReadAsync(cts.Token));
        Assert.Equal("live", await inactivePrunedReader.ReadAsync(cts.Token));

        foreach (var activeReader in activeReaders)
        {
            hub.Unsubscribe("orders", activeReader);
        }

        hub.Unsubscribe("orders-0", activeReplayReader);
        hub.Unsubscribe("orders-20", inactivePrunedReader);
    }

    [Fact]
    public async Task PublishAsync_WithReplay_DoesNotRemoveCandidateRepublishedDuringPrune()
    {
        var hub = new InMemoryRazorWireStreamHub();
        await hub.PublishAsync("orders-0", "old", new RazorWireStreamPublishOptions { Replay = true });

        var republished = 0;
        hub.BeforeReplayPruneCandidateForTesting = channel =>
        {
            if (channel == "orders-0" && Interlocked.Exchange(ref republished, 1) == 0)
            {
                hub.PublishAsync("orders-0", "new", new RazorWireStreamPublishOptions { Replay = true })
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
        };

        for (var index = 1; index <= 256; index++)
        {
            await hub.PublishAsync(
                $"orders-{index}",
                $"retained-{index}",
                new RazorWireStreamPublishOptions { Replay = true });
        }

        hub.BeforeReplayPruneCandidateForTesting = null;

        var replayReader = hub.Subscribe("orders-0", new RazorWireStreamSubscribeOptions { Replay = true });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Assert.Equal("old", await replayReader.ReadAsync(cts.Token));
        Assert.Equal("new", await replayReader.ReadAsync(cts.Token));
    }

    [Fact]
    public async Task PublishAsync_WithReplay_DoesNotPruneActiveReplayChannel()
    {
        var hub = new InMemoryRazorWireStreamHub();
        await hub.PublishAsync("orders-0", "retained-0", new RazorWireStreamPublishOptions { Replay = true });
        var activeReader = hub.Subscribe("orders-0", new RazorWireStreamSubscribeOptions { Replay = true });

        using var activeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Assert.Equal("retained-0", await activeReader.ReadAsync(activeCts.Token));

        for (var index = 1; index < 260; index++)
        {
            await hub.PublishAsync(
                $"orders-{index}",
                $"retained-{index}",
                new RazorWireStreamPublishOptions { Replay = true });
        }

        var lateReader = hub.Subscribe("orders-0", new RazorWireStreamSubscribeOptions { Replay = true });

        using var lateCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Assert.Equal("retained-0", await lateReader.ReadAsync(lateCts.Token));

        hub.Unsubscribe("orders-0", activeReader);
        hub.Unsubscribe("orders-0", lateReader);
    }

    [Fact]
    public async Task Unsubscribe_DoesNotClearReplayBuffer()
    {
        var hub = new InMemoryRazorWireStreamHub();
        await hub.PublishAsync("orders", "retained", new RazorWireStreamPublishOptions { Replay = true });
        var liveReader = hub.Subscribe("orders");

        hub.Unsubscribe("orders", liveReader);

        var diagnostics = hub.GetDiagnostics();
        Assert.Equal(0, diagnostics.LiveChannelCount);
        Assert.Equal(1, diagnostics.ReplayChannelCount);

        var replayReader = hub.Subscribe("orders", new RazorWireStreamSubscribeOptions { Replay = true });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Assert.Equal("retained", await replayReader.ReadAsync(cts.Token));
    }

    [Fact]
    public async Task Subscribe_DuringLiveChannelRetirement_DoesNotOrphanNewSubscriber()
    {
        var hub = new InMemoryRazorWireStreamHub();
        using var retired = new ManualResetEventSlim();
        using var releaseCleanup = new ManualResetEventSlim();
        var hookCalls = 0;
        hub.AfterLiveChannelRetiredForTesting = () =>
        {
            if (Interlocked.Increment(ref hookCalls) != 1)
            {
                return;
            }

            retired.Set();
            Assert.True(releaseCleanup.Wait(TimeSpan.FromSeconds(5)));
        };

        var firstReader = hub.Subscribe("orders");
        var unsubscribeTask = Task.Run(() => hub.Unsubscribe("orders", firstReader));
        Assert.True(retired.Wait(TimeSpan.FromSeconds(5)));

        var secondReader = hub.Subscribe("orders");
        releaseCleanup.Set();
        await unsubscribeTask.WaitAsync(TimeSpan.FromSeconds(5));
        hub.AfterLiveChannelRetiredForTesting = null;

        await hub.PublishAsync("orders", "live");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Assert.Equal("live", await secondReader.ReadAsync(cts.Token));

        hub.Unsubscribe("orders", secondReader);

        var diagnostics = hub.GetDiagnostics();
        Assert.Equal(0, diagnostics.LiveChannelCount);
        Assert.Equal(0, diagnostics.LiveSubscriberCount);
        Assert.Equal(0, diagnostics.SubscriptionCount);
    }
}
