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
    public async Task PublishAsync_WithNoSubscribers_CompletesWithoutErrors()
    {
        // Arrange
        var hub = new InMemoryRazorWireStreamHub();

        // Act + Assert
        await hub.PublishAsync("missing", "payload");
    }
}
