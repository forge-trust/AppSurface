using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ForgeTrust.RazorWire.Streams;

/// <summary>
/// Provides an in-memory implementation of <see cref="IRazorWireStreamHub"/> using <see cref="Channel{T}"/> for message distribution.
/// </summary>
public class InMemoryRazorWireStreamHub : IRazorWireStreamHub
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ChannelWriter<string>, byte>> _channels = new();
    private readonly ConcurrentDictionary<ChannelReader<string>, ChannelWriter<string>> _readerToWriter = new();
    private readonly ConcurrentDictionary<ChannelWriter<string>, ChannelReader<string>> _writerToReader = new();

    /// <summary>
    /// Publishes a message to all subscribers of the specified channel. Any subscribers that are closed or unable to accept the message are removed during the process.
    /// </summary>
    /// <param name="channel">The name of the channel to publish to.</param>
    /// <param name="message">The message payload to deliver to subscribers.</param>
    /// <returns>`ValueTask.CompletedTask` on success, or a faulted `ValueTask` containing the exception if publishing failed.</returns>
    public ValueTask PublishAsync(string channel, string message)
    {
        try
        {
            if (_channels.TryGetValue(channel, out var subscribersDict))
            {
                var subscribers = subscribersDict.Keys.ToList();
                var closedSubscribers = subscribers.Where(subscriber => !subscriber.TryWrite(message)).ToList();

                // Cleanup closed subscribers
                foreach (var closed in closedSubscribers)
                {
                    // Explicitly attempt to complete to trigger any underlying cleanup logic
                    closed.TryComplete();

                    subscribersDict.TryRemove(closed, out _);

                    // Also remove the bidirectional mappings to prevent leaks
                    if (_writerToReader.TryRemove(closed, out var reader))
                    {
                        _readerToWriter.TryRemove(reader, out _);
                    }
                }

                // Prune empty channels to prevent unbounded memory growth.
                // We accept the minor race with Subscribe as Subscribe uses GetOrAdd.
                if (subscribersDict.IsEmpty)
                {
                    _channels.TryRemove(channel, out _);
                }
            }

            return ValueTask.CompletedTask;
        }
        catch (Exception exception)
        {
            return ValueTask.FromException(exception);
        }
    }

    /// <summary>
    /// Subscribes to a named channel and returns a reader that receives messages published to that channel.
    /// The subscription uses an in-memory bounded buffer with capacity 100 that drops the oldest messages when full.
    /// </summary>
    /// <param name="channel">The name of the channel to subscribe to.</param>
    /// <returns>A <see cref="ChannelReader{String}"/> that yields messages published to the specified channel until the subscription is removed or the writer is completed.</returns>
    public ChannelReader<string> Subscribe(string channel)
    {
        var subscriber = Channel.CreateBounded<string>(
            new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });

        _readerToWriter.TryAdd(subscriber.Reader, subscriber.Writer);
        _writerToReader.TryAdd(subscriber.Writer, subscriber.Reader);

        var subscribers = _channels.GetOrAdd(channel, _ => new ConcurrentDictionary<ChannelWriter<string>, byte>());
        subscribers.TryAdd(subscriber.Writer, 0);

        return subscriber.Reader;
    }

    /// <summary>
    /// Unregisters a subscriber from the specified channel and completes its associated writer.
    /// </summary>
    /// <param name="channel">The name of the channel to remove the subscriber from.</param>
    /// <param name="reader">The subscriber's <see cref="ChannelReader{String}"/>; its paired writer will be completed and removed from channel tracking.</param>
    public void Unsubscribe(string channel, ChannelReader<string> reader)
    {
        if (_readerToWriter.TryRemove(reader, out var writer))
        {
            _writerToReader.TryRemove(writer, out _);
            writer.TryComplete();
            if (_channels.TryGetValue(channel, out var subscribers))
            {
                subscribers.TryRemove(writer, out _);
            }
        }
    }
}
