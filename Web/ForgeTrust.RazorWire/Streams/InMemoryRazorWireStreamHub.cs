using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ForgeTrust.RazorWire.Streams;

/// <summary>
/// Provides an in-memory implementation of <see cref="IRazorWireStreamHub"/> using <see cref="Channel{T}"/> for message distribution.
/// </summary>
public class InMemoryRazorWireStreamHub : IRazorWireStreamHub
{
    private const int ReplayCapacity = 25;
    private const int MaxReplayChannels = 256;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ChannelWriter<string>, byte>> _channels = new();
    private readonly ConcurrentDictionary<ChannelReader<string>, ChannelWriter<string>> _readerToWriter = new();
    private readonly ConcurrentDictionary<ChannelWriter<string>, ChannelReader<string>> _writerToReader = new();
    private readonly ConcurrentDictionary<string, Queue<string>> _replayMessages = new();
    private readonly ConcurrentDictionary<string, object> _replayLocks = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _replayTouchedUtc = new();

    /// <summary>
    /// Publishes a message to all subscribers of the specified channel. Any subscribers that are closed or unable to accept the message are removed during the process.
    /// </summary>
    /// <param name="channel">The name of the channel to publish to.</param>
    /// <param name="message">The message payload to deliver to subscribers.</param>
    /// <returns>`ValueTask.CompletedTask` on success, or a faulted `ValueTask` containing the exception if publishing failed.</returns>
    public ValueTask PublishAsync(string channel, string message)
    {
        return PublishAsync(channel, message, options: null);
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(string channel, string message, RazorWireStreamPublishOptions? options)
    {
        try
        {
            if (options?.Replay == true)
            {
                var gate = _replayLocks.GetOrAdd(channel, _ => new object());
                lock (gate)
                {
                    AddReplayMessageLocked(channel, message);
                    PublishLiveMessage(channel, message);
                }

                PruneReplayStateIfNeeded();
            }
            else
            {
                PublishLiveMessage(channel, message);
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
        return Subscribe(channel, options: null);
    }

    /// <inheritdoc />
    public ChannelReader<string> Subscribe(string channel, RazorWireStreamSubscribeOptions? options)
    {
        var subscriber = Channel.CreateBounded<string>(
            new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });

        _readerToWriter.TryAdd(subscriber.Reader, subscriber.Writer);
        _writerToReader.TryAdd(subscriber.Writer, subscriber.Reader);

        if (options?.Replay == true)
        {
            var gate = _replayLocks.GetOrAdd(channel, _ => new object());
            lock (gate)
            {
                var subscribers = _channels.GetOrAdd(
                    channel,
                    _ => new ConcurrentDictionary<ChannelWriter<string>, byte>());
                foreach (var message in GetReplayMessagesLocked(channel))
                {
                    subscriber.Writer.TryWrite(message);
                }

                subscribers.TryAdd(subscriber.Writer, 0);
            }

            return subscriber.Reader;
        }

        var liveSubscribers = _channels.GetOrAdd(channel, _ => new ConcurrentDictionary<ChannelWriter<string>, byte>());
        liveSubscribers.TryAdd(subscriber.Writer, 0);
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
                if (subscribers.IsEmpty)
                {
                    _channels.TryRemove(channel, out _);
                    if (!_replayMessages.ContainsKey(channel))
                    {
                        _replayLocks.TryRemove(channel, out _);
                        _replayTouchedUtc.TryRemove(channel, out _);
                    }
                }
            }
        }
    }

    private void PublishLiveMessage(string channel, string message)
    {
        if (!_channels.TryGetValue(channel, out var subscribersDict))
        {
            return;
        }

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

    private void AddReplayMessageLocked(string channel, string message)
    {
        var messages = _replayMessages.GetOrAdd(channel, _ => new Queue<string>());
        messages.Enqueue(message);
        while (messages.Count > ReplayCapacity)
        {
            messages.Dequeue();
        }

        _replayTouchedUtc[channel] = DateTimeOffset.UtcNow;
    }

    private IReadOnlyList<string> GetReplayMessagesLocked(string channel)
    {
        if (!_replayMessages.TryGetValue(channel, out var messages))
        {
            return [];
        }

        return messages.ToArray();
    }

    private void PruneReplayStateIfNeeded()
    {
        if (_replayMessages.Count <= MaxReplayChannels)
        {
            return;
        }

        foreach (var channel in _replayTouchedUtc
            .OrderBy(item => item.Value)
            .Select(item => item.Key)
            .Take(_replayMessages.Count - MaxReplayChannels))
        {
            if (_channels.TryGetValue(channel, out var subscribers) && !subscribers.IsEmpty)
            {
                continue;
            }

            _replayMessages.TryRemove(channel, out _);
            _replayLocks.TryRemove(channel, out _);
            _replayTouchedUtc.TryRemove(channel, out _);
        }
    }
}
